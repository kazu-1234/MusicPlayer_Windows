using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using MusicPlayer.Models;
using Newtonsoft.Json;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 音楽ライブラリ（曲・アルバム・アーティストの一覧）を管理する。
    /// 起動時はキャッシュ（library.json）を即表示し、その後バックグラウンドで差分スキャンする。
    /// 更新日時とサイズが前回と同じファイルはタグを読み直さないので、2 回目以降の起動は軽い。
    /// </summary>
    public sealed class LibraryService
    {
        /// <summary>フォルダ名からアーティストを補うとき、アーティスト名とみなさない形式フォルダ名。</summary>
        private static readonly HashSet<string> FormatFolderNames =
            new(StringComparer.OrdinalIgnoreCase) { "FLAC", "MP3", "WAV", "AAC", "ALAC", "M4A" };

        /// <summary>
        /// キャッシュの形式・読み取り方法の版。タグの読み方を直したとき（例: WAV の文字化け対策）に上げると、
        /// 古いキャッシュを使わず全曲を読み直す。
        /// </summary>
        private const int CacheVersion = 7;

        private readonly Settings _settings;
        private readonly DispatcherQueue _ui;
        private readonly string _cachePath = Path.Combine(AppConfig.AppDataFolder, $"library.v{CacheVersion}.json");
        private readonly object _scanLock = new();
        private CancellationTokenSource? _scanCts;

        // 一覧は丸ごと差し替える（読み取り側はロック不要で、途中状態を見ることがない）
        private List<Track> _tracks = new();
        private Dictionary<string, Track> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public LibraryService(Settings settings, DispatcherQueue ui)
        {
            _settings = settings;
            _ui = ui;
        }

        public IReadOnlyList<Track> Tracks => _tracks;
        public IReadOnlyList<Album> Albums { get; private set; } = Array.Empty<Album>();
        public IReadOnlyList<Artist> Artists { get; private set; } = Array.Empty<Artist>();

        /// <summary>ライブラリに含まれる形式名（形式フィルタの選択肢）。</summary>
        public IReadOnlyList<string> Formats { get; private set; } = Array.Empty<string>();

        public bool IsScanning { get; private set; }

        /// <summary>走査済みファイル数（スキャン中の進捗表示用）。</summary>
        public int ScanProcessed { get; private set; }

        /// <summary>一覧が変わった（UI スレッドで発火）。</summary>
        public event Action? Changed;

        /// <summary>スキャン開始・進捗・終了（UI スレッドで発火）。</summary>
        public event Action? ScanStateChanged;

        /// <summary>キャッシュを読み込んで一覧を作る（起動直後に 1 回）。</summary>
        public async Task LoadCacheAsync()
        {
            List<Track>? cached = await Task.Run(() =>
            {
                // 旧版のキャッシュは読み方が違うので捨てる
                foreach (string old in Directory.Exists(AppConfig.AppDataFolder)
                             ? Directory.EnumerateFiles(AppConfig.AppDataFolder, "library*.json")
                             : Enumerable.Empty<string>())
                {
                    if (!string.Equals(old, _cachePath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(old); } catch { }
                    }
                }

                try
                {
                    if (!File.Exists(_cachePath))
                        return null;
                    return JsonConvert.DeserializeObject<List<Track>>(File.ReadAllText(_cachePath));
                }
                catch
                {
                    // 壊れたキャッシュは捨てて、フルスキャンに任せる
                    return null;
                }
            });

            if (cached != null)
                await PublishAsync(cached);
        }

        /// <summary>ライブラリフォルダを走査して差分を反映する。実行中に呼ばれたら前の走査を止めてやり直す。</summary>
        public async Task ScanAsync()
        {
            CancellationTokenSource cts;
            lock (_scanLock)
            {
                _scanCts?.Cancel();
                _scanCts = cts = new CancellationTokenSource();
            }

            IsScanning = true;
            ScanProcessed = 0;
            ScanStateChanged?.Invoke();

            try
            {
                List<string> roots = GetRoots();
                Dictionary<string, Track> previous = _byPath;
                List<Track>? result = await Task.Run(() => ScanCore(roots, previous, cts.Token), cts.Token);
                if (result != null && !cts.IsCancellationRequested)
                {
                    await PublishAsync(result);
                    await Task.Run(() => SaveCache(result));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // 走査の失敗（フォルダが消えた・アクセス拒否など）でアプリを落とさない。原因はログで追えるようにする
                AppRuntime.AppendLifetimeLog($"library-scan-failed {ex}");
            }
            finally
            {
                if (ReferenceEquals(_scanCts, cts))
                {
                    IsScanning = false;
                    ScanStateChanged?.Invoke();
                }
            }
        }

        public Track? FindByPath(string path) =>
            _byPath.TryGetValue(path, out var track) ? track : null;

        /// <summary>
        /// ライブラリにあればその情報を、無ければファイルからタグを読んで返す
        /// （ライブラリ外の曲をプレイリストや変換で扱うため）。
        /// </summary>
        public Track GetOrRead(string path) => FindByPath(path) ?? ReadWithFallback(path, GetRoots());

        /// <summary>
        /// タグ編集・変換・CD 取り込みの後に呼び、指定ファイルだけ読み直して一覧に反映する。
        /// ライブラリフォルダの外にあるファイルは追加しない。
        /// </summary>
        public async Task RefreshFilesAsync(IEnumerable<string> paths)
        {
            List<string> roots = GetRoots();
            var targets = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var previous = _byPath;
            var updated = await Task.Run(() =>
            {
                var map = new Dictionary<string, Track>(previous, StringComparer.OrdinalIgnoreCase);
                foreach (string path in targets)
                {
                    try
                    {
                        if (!File.Exists(path))
                            map.Remove(path);
                        else if (IsUnderRoots(path, roots) && IsAudioFile(path))
                            map[path] = ReadWithFallback(path, roots);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 書き込み直後でまだ開けないファイルなどは、次の再スキャンに任せる
                    }
                }
                return map.Values.ToList();
            });

            await PublishAsync(updated);
            _ = Task.Run(() => SaveCache(updated));
        }

        public static bool IsAudioFile(string path) =>
            AppConfig.AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        private List<string> GetRoots()
        {
            var roots = _settings.LibraryFolders.Where(Directory.Exists).Select(Path.GetFullPath).ToList();
            // 親子関係にあるフォルダが両方登録されていると二重に数えるので、子の方を除く
            return roots.Where(r => !roots.Any(o => o != r && IsUnderRoots(r, new List<string> { o }))).ToList();
        }

        private static bool IsUnderRoots(string path, List<string> roots) =>
            roots.Any(r => path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        /// <summary>バックグラウンドスレッドで実行する走査本体。</summary>
        private List<Track>? ScanCore(List<string> roots, Dictionary<string, Track> previous, CancellationToken token)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,  // アクセス権の無いフォルダで例外にしない
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            // ルートごとに列挙する。1 つのフォルダで失敗（ネットワークドライブの切断など）しても他は続ける
            var files = roots
                .SelectMany(r =>
                {
                    try
                    {
                        return Directory.EnumerateFiles(r, "*", options).Where(IsAudioFile).ToList();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppRuntime.AppendLifetimeLog($"library-enum-failed {r} {ex.Message}");
                        return new List<string>();
                    }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new ConcurrentBag<Track>();
            int processed = 0;

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = AppConfig.ScanParallelism, CancellationToken = token }, path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    // 前回と同じ更新日時・サイズならタグを読まずに再利用する（差分スキャンの要）
                    if (previous.TryGetValue(path, out var old)
                        && old.LastWriteTicks == info.LastWriteTimeUtc.Ticks
                        && old.FileSize == info.Length)
                    {
                        results.Add(old);
                    }
                    else
                    {
                        results.Add(ReadWithFallback(path, roots));
                    }
                }
                catch
                {
                    // 走査中に消えたファイルなどは飛ばす
                }

                int n = Interlocked.Increment(ref processed);
                if (n % 200 == 0)
                {
                    _ui.TryEnqueue(() =>
                    {
                        ScanProcessed = n;
                        ScanStateChanged?.Invoke();
                    });
                }
            });

            return token.IsCancellationRequested ? null : results.ToList();
        }

        /// <summary>
        /// タグを読み、空の項目をフォルダ構成（…\アーティスト\アルバム\曲）から補う。
        /// タグの無い WAV でもアルバム・アーティスト別に並ぶようにするため。
        /// </summary>
        private static Track ReadWithFallback(string path, List<string> roots)
        {
            Track track = TagService.ReadTrack(path);
            string? albumDir = Path.GetDirectoryName(path);
            if (albumDir == null || IsRoot(albumDir, roots))
            {
                FillReadings(track);
                return track;
            }

            if (string.IsNullOrWhiteSpace(track.Album))
                track.Album = Path.GetFileName(albumDir);

            string? artistDir = Path.GetDirectoryName(albumDir);
            if (string.IsNullOrWhiteSpace(track.Artist) && artistDir != null && !IsRoot(artistDir, roots)
                && !FormatFolderNames.Contains(Path.GetFileName(artistDir)))
            {
                track.Artist = Path.GetFileName(artistDir);
            }

            FillReadings(track);
            return track;
        }

        /// <summary>
        /// 名前順の並べ替えに使う読みを作る（漢字は Windows の辞書でかなにする）。
        /// スキャンのとき（バックグラウンド）に 1 回だけ作り、キャッシュに保存する。
        /// </summary>
        private static void FillReadings(Track track)
        {
            track.TitleReading = ReadingService.For(track.DisplayTitle, track.TitleSort);
            track.ArtistReading = ReadingService.For(track.DisplayArtist, track.ArtistSort);
            track.AlbumReading = ReadingService.For(track.DisplayAlbum, track.AlbumSort);
        }

        private static bool IsRoot(string dir, List<string> roots) =>
            roots.Any(r => string.Equals(r.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        /// <summary>曲一覧からアルバム・アーティストを組み立て、UI スレッドで差し替えて通知する。</summary>
        private async Task PublishAsync(List<Track> tracks)
        {
            var built = await Task.Run(() => BuildIndexes(tracks));
            var done = new TaskCompletionSource();
            bool queued = _ui.TryEnqueue(() =>
            {
                try
                {
                    _tracks = built.Tracks;
                    _byPath = built.ByPath;
                    Albums = built.Albums;
                    Artists = built.Artists;
                    Formats = built.Formats;
                    Changed?.Invoke();
                }
                catch (Exception ex)
                {
                    // 画面側の更新で失敗しても、データの差し替えは済んでいる。ログに残して続ける
                    AppRuntime.AppendLifetimeLog($"library-changed-handler-failed {ex}");
                }
                finally
                {
                    // ここを必ず通さないと、await done.Task が永遠に終わらずスキャン中のままになる
                    done.TrySetResult();
                }
            });
            if (queued)
                await done.Task;
        }

        private static (List<Track> Tracks, Dictionary<string, Track> ByPath, List<Album> Albums, List<Artist> Artists, List<string> Formats)
            BuildIndexes(List<Track> tracks)
        {
            // 古いキャッシュなどで読みが無い曲は、ここで作る（並べ替えに必要）
            foreach (var t in tracks)
            {
                if (t.TitleReading.Length == 0 && t.DisplayTitle.Length > 0)
                    FillReadings(t);
            }

            // 既定の並び（名前順・昇順）。漢字は読みで比べる（SortService の設定に従う）
            var byName = new SortSpec();
            var sortedTracks = SortService.Sort(tracks, byName);
            var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in sortedTracks)
                byPath[t.Path] = t;

            // 共演表記をまとめるための、ライブラリ全体の「単独のアーティストとしての曲数」
            var artistResolver = new ArtistNames.Resolver(sortedTracks.Select(t => t.DisplayArtist));

            // アルバムのキー: アルバムアーティストがあればそれで、無ければフォルダでまとめる。
            // 形式もキーに含め、FLAC 版と MP3 版を別アルバムにする（形式フィルタで切り替えるため）。
            var albums = sortedTracks
                .GroupBy(t => (string.IsNullOrWhiteSpace(t.AlbumArtist)
                                  ? "dir:" + Path.GetDirectoryName(t.Path)?.ToLowerInvariant()
                                  : "aa:" + t.AlbumArtist.ToLowerInvariant())
                              + "|" + t.DisplayAlbum.ToLowerInvariant() + "|" + t.Format)
                .Select(g =>
                {
                    var list = g.ToList();
                    // 共演表記（AI Feat. カンナ など）の主アーティストを、このアルバムの中での出現数から決める
                    var primaryOf = artistResolver.ForAlbum(list.Select(t => t.DisplayArtist).ToList());
                    foreach (var t in list)
                        t.PrimaryArtist = primaryOf(t.DisplayArtist);

                    var album = new Album(g.Key, list[0].DisplayAlbum, AlbumArtistOf(list, primaryOf), list[0].Format, list);
                    album.TitleReading = list[0].AlbumReading;
                    album.ArtistReading = ReadingService.For(album.Artist);
                    return album;
                })
                .ToList();
            albums = SortService.Sort(albums, byName);

            var albumOfTrack = new Dictionary<Track, Album>();
            foreach (var album in albums)
                foreach (var t in album.Tracks)
                    albumOfTrack[t] = album;

            // アーティスト一覧は主アーティストでまとめる（「AI Feat. カンナ」の曲は「AI」の中に入る）
            var artists = sortedTracks
                .GroupBy(t => t.PrimaryArtist, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var list = g.ToList();
                    var artistAlbums = list.Select(t => albumOfTrack[t]).Distinct().ToList();
                    // 読みは主アーティスト名から作る（曲の ArtistReading は共演表記全体の読みのため）
                    string reading = list.FirstOrDefault(t => string.Equals(t.DisplayArtist, g.Key, StringComparison.OrdinalIgnoreCase))?.ArtistReading
                                     ?? ReadingService.For(g.Key);
                    return new Artist(g.Key, artistAlbums, list) { Reading = reading };
                })
                .ToList();
            artists = SortService.Sort(artists, byName);

            var formats = sortedTracks.Select(t => t.Format).Distinct().OrderBy(f => f).ToList();
            return (sortedTracks, byPath, albums, artists, formats);
        }

        /// <summary>
        /// アルバムの表示アーティスト。アルバムアーティストのタグがあればそれ（共演表記なら主アーティスト）。
        /// 無ければ曲の主アーティストが 1 名にそろえばその名前、バラバラなら「複数のアーティスト」。
        /// （feat. の曲が混ざっていても、主アーティストでそろえるので「複数のアーティスト」にならない）
        /// </summary>
        private static string AlbumArtistOf(List<Track> tracks, Func<string, string> primaryOf)
        {
            string? albumArtist = tracks.Select(t => t.AlbumArtist).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            if (albumArtist != null)
                return primaryOf(albumArtist);

            var artists = tracks.Select(t => t.PrimaryArtist).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return artists.Count == 1 ? artists[0] : Strings.Get("Common_VariousArtists");
        }

        private void SaveCache(List<Track> tracks)
        {
            try
            {
                Directory.CreateDirectory(AppConfig.AppDataFolder);
                string tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(tracks));
                File.Move(tmp, _cachePath, overwrite: true);
            }
            catch
            {
                // キャッシュ保存の失敗は次回のフルスキャンで回復するので無視する
            }
        }
    }
}
