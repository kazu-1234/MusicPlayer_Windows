using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MusicPlayer.Models;
using Windows.Storage;

namespace MusicPlayer.Services
{
    /// <summary>
    /// m3u / m3u8 プレイリストの読み書き。
    /// 既存ファイル（UTF-8 BOM なし・絶対パス・「#EXTINF:秒,曲名 - アーティスト」）と同じ形式で書き戻し、
    /// 文字コードと改行コードも元ファイルに合わせる。
    /// </summary>
    public sealed class PlaylistService
    {
        private readonly Settings _settings;
        private readonly LibraryService _library;

        static PlaylistService()
        {
            // .NET では Shift_JIS が既定で使えないため、古い m3u 用にコードページを登録する
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public PlaylistService(Settings settings, LibraryService library)
        {
            _settings = settings;
            _library = library;
        }

        public string Folder => _settings.PlaylistFolder;

        /// <summary>プレイリストの保存・作成・削除があった（一覧の再読み込み用）。</summary>
        public event Action? Changed;

        /// <summary>保存先フォルダの変更など、ファイル操作以外で一覧が変わったときに呼ぶ（サイドバーを作り直させる）。</summary>
        public void NotifyChanged() => Changed?.Invoke();

        /// <summary>フォルダ内のプレイリスト一覧。曲数は「パス行の数」を数えるだけで、タグは読まない。</summary>
        public Task<List<PlaylistInfo>> ListAsync() => Task.Run(() =>
        {
            var result = new List<PlaylistInfo>();
            if (!Directory.Exists(Folder))
                return result;

            foreach (string path in Directory.EnumerateFiles(Folder)
                         .Where(IsPlaylistFile)
                         .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
            {
                int count = 0;
                try
                {
                    string text = Decode(File.ReadAllBytes(path), path, out _);
                    count = SplitLines(text).Count(l => l.Length > 0 && !l.StartsWith('#'));
                }
                catch
                {
                }
                result.Add(new PlaylistInfo(Path.GetFileNameWithoutExtension(path), path, count));
            }
            return result;
        });

        /// <summary>プレイリストを読み込み、各行を曲情報に解決する（ライブラリに無い曲はファイルから読む）。</summary>
        public Task<PlaylistDocument> LoadAsync(string path) => Task.Run(() =>
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text = Decode(bytes, path, out Encoding encoding);
            var doc = new PlaylistDocument
            {
                Path = path,
                Encoding = encoding,
                NewLine = text.Contains("\r\n") ? "\r\n" : "\n",
                HasHeader = false
            };

            string baseDir = Path.GetDirectoryName(path) ?? "";
            string? pendingExtInf = null;
            foreach (string raw in SplitLines(text))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    doc.HasHeader = true;
                    continue;
                }

                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingExtInf = line;
                    continue;
                }

                if (line.StartsWith('#'))
                    continue; // その他の拡張コメントは使わない

                string fullPath = ResolvePath(line, baseDir);
                bool exists = File.Exists(fullPath);
                Track track = exists ? _library.GetOrRead(fullPath) : TrackFromExtInf(fullPath, pendingExtInf);
                doc.Entries.Add(new PlaylistEntry(line, pendingExtInf, track, exists));
                pendingExtInf = null;
            }

            return doc;
        });

        /// <summary>
        /// 保存する。書き途中で落ちても元ファイルが壊れないよう、一時ファイルに書いてから置き換える。
        /// </summary>
        public async Task SaveAsync(PlaylistDocument doc)
        {
            await Task.Run(() =>
            {
                var sb = new StringBuilder();
                if (doc.HasHeader)
                    sb.Append("#EXTM3U").Append(doc.NewLine);

                foreach (var entry in doc.Entries)
                {
                    sb.Append(entry.ExtInfLine ?? BuildExtInf(entry.Track)).Append(doc.NewLine);
                    sb.Append(entry.RawPath).Append(doc.NewLine);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(doc.Path)!);
                BackupBeforeSave(doc.Path);
                string tmp = doc.Path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), doc.Encoding);
                File.Move(tmp, doc.Path, overwrite: true);
            });
            Changed?.Invoke();
        }

        /// <summary>
        /// プレイリストごとのバックアップの置き場所（%AppData%\MusicPlayer\playlist-backup\ファイル名\）。
        /// この中に「保存した日時.m3u」が保存のたびに増え、新しいものから AppConfig.PlaylistBackupCount 個だけ残る。
        /// </summary>
        private static string BackupDirOf(string playlistPath) =>
            Path.Combine(AppConfig.PlaylistBackupFolder, Path.GetFileName(playlistPath));

        /// <summary>以前の形式（1 回分だけ・フォルダを作らず直接置いていた）のバックアップのパス。</summary>
        private static string LegacyBackupPathOf(string playlistPath) =>
            Path.Combine(AppConfig.PlaylistBackupFolder, Path.GetFileName(playlistPath));

        /// <summary>
        /// 書き込む直前に、今の（保存前の）ファイルをバックアップとして残す。
        /// ファイル名は保存した日時（例: 20261018-153012-123.m3u）なので、名前順＝古い順になる。
        /// 増えすぎないよう、古いものから消して最新の AppConfig.PlaylistBackupCount 個だけ残す。
        /// 失敗しても保存自体は続ける（バックアップは保険なので、保存を止める理由にしない）。
        /// </summary>
        private static void BackupBeforeSave(string playlistPath)
        {
            if (!File.Exists(playlistPath))
                return; // 新規作成でまだファイルが無い
            try
            {
                string dir = BackupDirOf(playlistPath);
                MigrateLegacyBackup(playlistPath, dir);
                Directory.CreateDirectory(dir);

                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + Path.GetExtension(playlistPath);
                File.Copy(playlistPath, Path.Combine(dir, name), overwrite: true);

                // 古い順（ファイル名の昇順）に並べ、上限を超えた分を消す
                var files = Directory.GetFiles(dir).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
                for (int i = 0; i < files.Count - AppConfig.PlaylistBackupCount; i++)
                    File.Delete(files[i]);
            }
            catch (Exception ex)
            {
                AppRuntime.AppendLifetimeLog($"playlist-backup-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 以前の形式のバックアップ（playlist-backup\名前.m3u というファイル）が残っていれば、
        /// 新しい形式のフォルダの中へ「一番古い 1 個」として移す。
        /// 新しい形式ではフォルダが同じ名前になるので、先にファイルをどかしてからフォルダを作る。
        /// </summary>
        private static void MigrateLegacyBackup(string playlistPath, string dir)
        {
            string legacy = LegacyBackupPathOf(playlistPath);
            if (!File.Exists(legacy))
                return;

            string temp = legacy + ".old";
            File.Move(legacy, temp, overwrite: true);
            Directory.CreateDirectory(dir);
            // 「00000000-」で始めると、日時の名前より必ず前（＝一番古い）に並ぶ
            File.Move(temp, Path.Combine(dir, "00000000-000000-000" + Path.GetExtension(playlistPath)), overwrite: true);
        }

        /// <summary>名前変更・削除に合わせて、バックアップのフォルダを付け替える・消す（失敗はログだけ）。</summary>
        private static void TryBackupDirOp(Action op, string sourceDir)
        {
            try
            {
                if (Directory.Exists(sourceDir))
                    op();
            }
            catch (Exception ex)
            {
                AppRuntime.AppendLifetimeLog($"playlist-backup-op-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>空のプレイリストを作る。同名があれば (2) などを付ける。</summary>
        /// <param name="pickFolder">
        /// ライブラリフォルダが 1 つも無く、置き場所を決められないときに呼ぶフォルダ選択（null が返ればキャンセル）。
        /// </param>
        /// <returns>作ったファイルのパス。フォルダ選択をキャンセルしたら null。</returns>
        public async Task<string?> CreateAsync(string name, Func<Task<string?>> pickFolder)
        {
            if (!await EnsureFolderAsync(pickFolder))
                return null;
            string path = UniquePath(SanitizeFileName(name));
            await SaveAsync(new PlaylistDocument { Path = path, HasHeader = true });
            return path;
        }

        // ───────── プレイリスト用フォルダの決め方 ─────────

        /// <summary>
        /// ライブラリフォルダの直下に「Playlists」「プレイリスト」（AppConfig.PlaylistFolderNames）があれば、その 1 つ目を返す。
        /// 直下だけを見る（奥まで探すと、別の用途のフォルダを誤って拾うことがあるため）。
        /// </summary>
        public string? DetectFolder()
        {
            foreach (string root in _settings.LibraryFolders.Where(Directory.Exists))
            {
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        string name = Path.GetFileName(dir);
                        if (AppConfig.PlaylistFolderNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                            return dir;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 読めないフォルダは飛ばす
                }
            }
            return null;
        }

        /// <summary>
        /// ライブラリフォルダ内にプレイリスト用フォルダが見つかれば、そこへ切り替えて一覧を読み直させる。
        /// 起動時と、ライブラリフォルダを追加・削除したときに呼ぶ。見つからなければ何も変えない。
        /// </summary>
        /// <returns>切り替えたら true。</returns>
        public bool ApplyDetectedFolder()
        {
            string? found = DetectFolder();
            if (found == null)
                return false;
            // 既に同じフォルダを使っていれば何もしない（末尾の \ の有無の違いは同じとみなす）
            if (!string.IsNullOrEmpty(Folder)
                && string.Equals(Path.GetFullPath(found).TrimEnd('\\'), Path.GetFullPath(Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;

            SetFolder(found);
            return true;
        }

        /// <summary>
        /// 新しいプレイリストを作る前に、置き場所を決める。
        /// 1) 今のフォルダに既にプレイリストがあればそのまま（既存のものを置き去りにしない）
        /// 2) ライブラリ内の「Playlists」「プレイリスト」
        /// 3) 最初のライブラリフォルダの直下に「Playlists」を作る
        /// 4) ライブラリフォルダが無ければ選んでもらう
        /// </summary>
        private async Task<bool> EnsureFolderAsync(Func<Task<string?>> pickFolder)
        {
            if (Directory.Exists(Folder) && Directory.EnumerateFiles(Folder).Any(IsPlaylistFile))
                return true;

            string? folder = DetectFolder();
            if (folder == null)
            {
                string? firstRoot = _settings.LibraryFolders.FirstOrDefault(Directory.Exists);
                folder = firstRoot != null
                    ? Path.Combine(firstRoot, AppConfig.PlaylistFolderNames[0])
                    : await pickFolder();
            }
            if (folder == null)
                return false;

            Directory.CreateDirectory(folder);
            if (!string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
                SetFolder(folder);
            return true;
        }

        private void SetFolder(string folder)
        {
            _settings.PlaylistFolder = folder;
            _settings.Save();
            Changed?.Invoke(); // サイドバーの一覧を読み直させる
        }

        public string Rename(string path, string newName)
        {
            string ext = Path.GetExtension(path);
            string dest = Path.Combine(Path.GetDirectoryName(path)!, SanitizeFileName(newName) + ext);
            if (!string.Equals(dest, path, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest))
                    throw new IOException(Strings.Format("Playlist_NameExists", newName));
                File.Move(path, dest);
                // バックアップのフォルダも新しい名前に付け替える（古い名前のまま残さない）
                // 以前の形式のバックアップが残っていれば、先にフォルダへ移してから一緒に扱う
                TryBackupDirOp(() => MigrateLegacyBackup(path, BackupDirOf(path)), AppConfig.PlaylistBackupFolder);
                TryBackupDirOp(() => Directory.Move(BackupDirOf(path), BackupDirOf(dest)), BackupDirOf(path));
                Changed?.Invoke();
            }
            return dest;
        }

        /// <summary>ごみ箱へ移動する（完全削除しないので誤操作から戻せる）。</summary>
        public async Task DeleteAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await file.DeleteAsync(StorageDeleteOption.Default);
            // 以前の形式のバックアップが残っていれば、先にフォルダへ移してから一緒に扱う
            TryBackupDirOp(() => MigrateLegacyBackup(path, BackupDirOf(path)), AppConfig.PlaylistBackupFolder);
            TryBackupDirOp(() => Directory.Delete(BackupDirOf(path), recursive: true), BackupDirOf(path));
            Changed?.Invoke();
        }

        /// <summary>指定したプレイリストの末尾に曲を追加して保存する（ライブラリの右クリックから）。</summary>
        public async Task AppendAsync(string path, IEnumerable<Track> tracks)
        {
            var doc = await LoadAsync(path);
            foreach (var t in tracks)
                doc.Entries.Add(NewEntry(t));
            await SaveAsync(doc);
        }

        /// <summary>新しく追加する曲のエントリ。パスは既存ファイルに合わせて絶対パスで書く。</summary>
        public static PlaylistEntry NewEntry(Track track) => new(track.Path, null, track, true);

        public static bool IsPlaylistFile(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>既存ファイルと同じ「#EXTINF:秒,曲名 - アーティスト」形式。</summary>
        private static string BuildExtInf(Track track) =>
            $"#EXTINF:{(int)Math.Round(track.DurationSeconds)},{track.DisplayTitle} - {track.DisplayArtist}";

        /// <summary>ファイルが見つからない曲は、#EXTINF の「曲名 - アーティスト」から表示名を作る。</summary>
        private static Track TrackFromExtInf(string path, string? extInf)
        {
            var track = new Track { Path = path };
            if (extInf == null)
                return track;

            int comma = extInf.IndexOf(',');
            if (comma < 0)
                return track;

            if (double.TryParse(extInf.Substring(8, comma - 8), out double seconds))
                track.DurationSeconds = Math.Max(0, seconds);

            string label = extInf[(comma + 1)..];
            int sep = label.LastIndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                track.Title = label[..sep];
                track.Artist = label[(sep + 3)..];
            }
            else
            {
                track.Title = label;
            }
            return track;
        }

        private static string ResolvePath(string line, string baseDir)
        {
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var uri))
                return uri.LocalPath;

            string p = line.Replace('/', '\\');
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
        }

        /// <summary>
        /// 文字コード判定。BOM があればそれに従い、無ければ UTF-8 として厳密に読めるか試し、
        /// 読めなければ Shift_JIS（古い Windows の m3u）とみなす。
        /// </summary>
        private static string Decode(byte[] bytes, string path, out Encoding encoding)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                // throwOnInvalidBytes: true … UTF-8 として不正なバイトがあれば例外にして Shift_JIS 判定へ回す
                var strict = new UTF8Encoding(false, true);
                string text = strict.GetString(bytes);
                encoding = new UTF8Encoding(false);
                return text;
            }
            catch (DecoderFallbackException) when (!path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                encoding = Encoding.GetEncoding(932);
                return encoding.GetString(bytes);
            }
        }

        private static IEnumerable<string> SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r'));

        private string UniquePath(string baseName)
        {
            string path = Path.Combine(Folder, baseName + ".m3u");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(Folder, $"{baseName} ({i}).m3u");
            return path;
        }

        /// <summary>ファイル名に使えない文字（\ / : * ? " &lt; &gt; |）を「_」に置き換える。末尾のドット・空白も除く。</summary>
        public static string SanitizeFileName(string name)
        {
            var sb = new StringBuilder(name.Trim());
            foreach (char c in Path.GetInvalidFileNameChars())
                sb.Replace(c, '_');
            string result = sb.ToString().TrimEnd('.', ' ');
            return result.Length == 0 ? "_" : result;
        }
    }
}
