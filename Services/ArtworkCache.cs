using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Services
{
    /// <summary>
    /// ジャケット画像の縮小キャッシュ（%AppData%\MusicPlayer\art\）。
    /// 元画像（数 MB のこともある）を毎回デコードすると重いので、
    /// 一度だけ AppConfig.ArtworkCacheSize 四方の JPEG に縮めて保存し、以後はそれを表示する。
    /// </summary>
    public static class ArtworkCache
    {
        private static readonly SemaphoreSlim Gate = new(AppConfig.ArtworkParallelism);
        private static readonly string Folder = Path.Combine(AppConfig.AppDataFolder, "art");

        /// <summary>
        /// キー → キャッシュ画像のパス（画像が無いアルバムは null）を覚えておく。
        /// 一覧の行が表示されるたびに File.Exists（ディスク確認）を UI スレッドで呼ぶと、スクロールや並べ替えが重くなるため。
        /// </summary>
        private static readonly ConcurrentDictionary<string, string?> Known = new();

        /// <summary>曲のフォルダをキーにする（同じフォルダ＝同じアルバム、とみなす）。</summary>
        public static string KeyFor(string trackPath) =>
            (Path.GetDirectoryName(trackPath) ?? trackPath).ToLowerInvariant();

        /// <summary>
        /// キャッシュ済み画像のパスを返す。無ければ曲のタグ → フォルダ内画像の順に探して作る。
        /// 画像が見つからなかったことも「.none」ファイルで記録し、次回から探し直さない。
        /// </summary>
        public static async Task<string?> GetOrCreateAsync(string key, string sampleTrackPath)
        {
            // 2 回目以降はメモリの記録だけで答える（ディスクを確認しない）
            if (Known.TryGetValue(key, out string? known))
                return known;

            string hash = Hash(key);
            string jpgPath = Path.Combine(Folder, hash + ".jpg");
            string nonePath = Path.Combine(Folder, hash + ".none");

            // 初回の存在確認もディスク I/O なので UI スレッドから外す
            var (hasJpg, hasNone) = await Task.Run(() => (File.Exists(jpgPath), File.Exists(nonePath)));
            if (hasJpg) return Known[key] = jpgPath;
            if (hasNone) return Known[key] = null;

            await Gate.WaitAsync();
            try
            {
                // 待っている間に別の呼び出しが作り終えていることがある
                if (File.Exists(jpgPath)) return Known[key] = jpgPath;

                // タグの読み取りとファイル検索はディスク I/O なので UI スレッドから外す
                byte[]? source = await Task.Run(() => TagService.ReadCover(sampleTrackPath) ?? FindFolderImage(sampleTrackPath));
                Directory.CreateDirectory(Folder);
                if (source == null || !await TryWriteThumbnailAsync(source, jpgPath))
                {
                    await File.WriteAllBytesAsync(nonePath, Array.Empty<byte>());
                    return Known[key] = null;
                }

                return Known[key] = jpgPath;
            }
            catch
            {
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>タグ編集でジャケットを変えたとき、古いキャッシュを捨てて作り直させる。</summary>
        public static void Invalidate(string key)
        {
            Known.TryRemove(key, out _);
            string hash = Hash(key);
            TryDelete(Path.Combine(Folder, hash + ".jpg"));
            TryDelete(Path.Combine(Folder, hash + ".none"));
        }

        /// <summary>フォルダ内の folder.jpg / cover.png などを探し、無ければ最初の画像を使う。</summary>
        private static byte[]? FindFolderImage(string trackPath)
        {
            string? dir = Path.GetDirectoryName(trackPath);
            if (dir == null || !Directory.Exists(dir))
                return null;

            var images = Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (images.Count == 0)
                return null;

            string? preferred = images.FirstOrDefault(f =>
                AppConfig.FolderArtNames.Any(n => Path.GetFileNameWithoutExtension(f).StartsWith(n, StringComparison.OrdinalIgnoreCase)));
            return File.ReadAllBytes(preferred ?? images[0]);
        }

        /// <summary>
        /// 画像を縦横比を保ったまま縮小して JPEG で保存する（縮小処理は ImageUtil と共通）。
        /// </summary>
        private static async Task<bool> TryWriteThumbnailAsync(byte[] source, string destPath)
        {
            try
            {
                byte[] jpeg = await ImageUtil.ResizeToJpegAsync(source, AppConfig.ArtworkCacheSize);

                // 書き途中のファイルを表示しないよう、一時ファイルに書いてから置き換える
                string tmp = destPath + ".tmp";
                await File.WriteAllBytesAsync(tmp, jpeg);
                File.Move(tmp, destPath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string Hash(string key) =>
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
