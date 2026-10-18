using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 画像の縮小（Windows 標準の BitmapDecoder / BitmapEncoder を使うので追加ライブラリは不要）。
    /// ジャケットの縮小キャッシュ作成と、ファイルへ埋め込むジャケットの大きさ調整で共通に使う。
    /// </summary>
    public static class ImageUtil
    {
        /// <summary>
        /// 縦横比を保ったまま、長辺が maxSize 以下になるよう縮小して JPEG にする（元が小さければ拡大しない）。
        /// </summary>
        /// <param name="quality">JPEG の画質（0〜1）。null なら既定（エンコーダー任せ）。</param>
        public static async Task<byte[]> ResizeToJpegAsync(byte[] source, uint maxSize, double? quality = null)
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(source.AsBuffer());
            input.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(input);
            uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
            double scale = Math.Min(1.0, (double)maxSize / Math.Max(w, h));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)(w * scale)),
                ScaledHeight = Math.Max(1, (uint)(h * scale)),
                // Fant: 縮小時にギザギザが出にくい補間方式
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            // RespectExifOrientation: スマホ写真などの「回転情報」を反映した向きで取り出す
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder;
            if (quality is double q)
            {
                // ImageQuality はエンコーダー作成時のオプションでしか指定できない
                var options = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(q, Windows.Foundation.PropertyType.Single) } };
                encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            }
            else
            {
                encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            }
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            output.Seek(0);
            using var ms = new MemoryStream();
            await output.AsStreamForRead().CopyToAsync(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// 曲ファイルに埋め込むジャケットに整える。長辺が AppConfig.EmbeddedCoverMaxSize 以下ならそのまま返し
        /// （再圧縮で画質を落とさないため）、大きすぎる画像だけ縮小する。読めない画像はそのまま返す。
        /// </summary>
        public static async Task<byte[]> FitForEmbeddingAsync(byte[] source)
        {
            try
            {
                using var input = new InMemoryRandomAccessStream();
                await input.WriteAsync(source.AsBuffer());
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                if (Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight) <= AppConfig.EmbeddedCoverMaxSize)
                    return source;

                return await ResizeToJpegAsync(source, AppConfig.EmbeddedCoverMaxSize, AppConfig.EmbeddedCoverJpegQuality);
            }
            catch (Exception ex)
            {
                AppRuntime.AppendLifetimeLog($"cover-resize-failed {ex.GetType().Name}: {ex.Message}");
                return source;
            }
        }
    }
}
