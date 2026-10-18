using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Models;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 同梱の ffmpeg.exe を別プロセスで起動して音声を変換する。
    /// 進捗は「-progress pipe:1」で標準出力に流れてくる out_time_us（再生位置）を読み、曲の長さで割って求める。
    /// </summary>
    public static class TranscodeService
    {
        /// <summary>ffmpeg.exe のパス。exe と同じフォルダの ffmpeg\ を探し、無ければ PATH も見る。見つからなければ null。</summary>
        public static string? FfmpegPath
        {
            get
            {
                string bundled = Path.Combine(AppContext.BaseDirectory, AppConfig.FfmpegRelativePath);
                if (File.Exists(bundled))
                    return bundled;

                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                return pathEnv?.Split(';')
                    .Select(dir => Path.Combine(dir.Trim(), "ffmpeg.exe"))
                    .FirstOrDefault(File.Exists);
            }
        }

        public static bool IsAvailable => FfmpegPath != null;

        /// <summary>
        /// 1 ファイルを変換し、最後にタグとジャケットを TagLib でコピーする。
        /// 変換途中の不完全なファイルを残さないよう、一時ファイルへ出力してから名前を変える。
        /// </summary>
        /// <param name="durationSeconds">進捗計算用の曲の長さ（不明なら 0 → 進捗は出ない）。</param>
        /// <param name="progress">0.0〜1.0 の進捗。</param>
        public static async Task ConvertAsync(
            string inputPath,
            string outputPath,
            AudioFormat format,
            QualityPreset preset,
            double durationSeconds,
            IProgress<double>? progress,
            CancellationToken token)
        {
            string ffmpeg = FfmpegPath ?? throw new InvalidOperationException(Strings.Get("Convert_FfmpegMissing"));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // 拡張子を保ったまま一時名にする（ffmpeg は拡張子で出力コンテナを決めるため）
            string tmpPath = Path.Combine(Path.GetDirectoryName(outputPath)!,
                "~" + Path.GetFileNameWithoutExtension(outputPath) + ".part" + Path.GetExtension(outputPath));

            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-i", inputPath,
                // 音声の 1 本目だけを使う（ジャケット画像のストリームは後で TagLib で付け直す）
                "-map", "0:a:0",
                "-map_metadata", "0",
                "-vn"
            };
            args.AddRange(CodecArgs(format, preset, inputPath));
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats", tmpPath });

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // ArgumentList は 1 要素ずつ正しくクォートしてくれる（日本語・空白を含むパス対策）
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            var errorLog = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                // エラー表示用に末尾だけ残す（長い出力でメモリを使わないように）
                if (e.Data != null && errorLog.Length < 8000)
                    errorLog.AppendLine(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();

            try
            {
                using (token.Register(() => TryKill(process)))
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                    {
                        // 例: out_time_us=12345678（マイクロ秒）
                        if (durationSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                            && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                        {
                            progress?.Report(Math.Clamp(us / 1_000_000.0 / durationSeconds, 0, 1));
                        }
                    }
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(LastLines(errorLog.ToString(), 3));

                // ffmpeg のメタデータ引き継ぎは形式間で漏れがあるため、TagLib で揃え直す
                try { TagService.CopyTags(inputPath, tmpPath); } catch { }

                File.Move(tmpPath, outputPath, overwrite: true);
                progress?.Report(1);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        /// <summary>出力形式ごとの ffmpeg のエンコーダ指定。</summary>
        private static IEnumerable<string> CodecArgs(AudioFormat format, QualityPreset preset, string inputPath)
        {
            switch (format)
            {
                case AudioFormat.Wav:
                    // 元が 24bit 以上なら 24bit のまま、それ以外は CD と同じ 16bit で書く
                    return new[] { "-c:a", SourceBitDepth(inputPath) > 16 ? "pcm_s24le" : "pcm_s16le" };
                case AudioFormat.Flac:
                    return new[] { "-c:a", "flac", "-compression_level", AppConfig.FlacCompressionLevel.ToString(CultureInfo.InvariantCulture) };
                case AudioFormat.Mp3:
                    // libmp3lame の固定ビットレート。ID3v2.3 は Windows のエクスプローラーで表示できる版
                    return new[] { "-c:a", "libmp3lame", "-b:a", $"{AudioFormatInfo.Bitrate(format, preset)}k", "-id3v2_version", "3" };
                case AudioFormat.Aac:
                    // ffmpeg 内蔵の AAC エンコーダ。faststart で再生開始に必要な情報をファイル先頭へ置く
                    return new[] { "-c:a", "aac", "-b:a", $"{AudioFormatInfo.Bitrate(format, preset)}k", "-movflags", "+faststart" };
                default:
                    return new[] { "-c:a", "alac", "-movflags", "+faststart" };
            }
        }

        private static int SourceBitDepth(string path)
        {
            try
            {
                using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
                return file.Properties?.BitsPerSample ?? 16;
            }
            catch
            {
                return 16;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        private static string LastLines(string text, int count) =>
            string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count).Select(l => l.Trim()));
    }
}
