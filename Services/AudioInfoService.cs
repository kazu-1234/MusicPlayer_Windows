using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicPlayer.Models;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 再生中画面の左下に出す、音源ファイルの詳細（音質・サイズなど）。
    /// 0 や空の項目は「分からない」を意味し、表示しない。
    /// </summary>
    public sealed class AudioInfo
    {
        public uint Year { get; init; }
        public string Format { get; init; } = "";
        public string Codec { get; init; } = "";
        public double DurationSeconds { get; init; }
        public int SampleRate { get; init; }
        public int BitsPerSample { get; init; }
        public int Channels { get; init; }
        public int BitrateKbps { get; init; }
        public long FileSize { get; init; }
        public bool IsLossless { get; init; }
    }

    /// <summary>
    /// 音源ファイルからサンプルレート・ビット深度などを読む（TagLib#）。
    /// ライブラリキャッシュ（library.json）には入れず、再生中の曲が変わったときだけ 1 ファイル読む。
    /// こうすると何千曲分のキャッシュ形式を変えずに済み、読むのも 1 曲分なので一瞬で終わる。
    /// </summary>
    public static class AudioInfoService
    {
        /// <summary>中身がそのまま（圧縮で音を削っていない）形式の拡張子。M4A は ALAC のときだけロスレスなので別に判定する。</summary>
        private static readonly HashSet<string> LosslessExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".aiff", ".aif" };

        /// <summary>ファイルの詳細を読む。重いディスク I/O なので UI スレッドから呼ばず Task.Run で使う。</summary>
        public static AudioInfo Read(Track track)
        {
            string path = track.Path;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            try
            {
                // PictureLazy: ジャケット画像は読まない（詳細表示には不要で、読むと遅くなる）
                using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
                var props = file.Properties;
                // Codecs の Description は「Flac Audio」「MPEG Version 1 Audio, Layer 3」のような説明文
                string codec = props?.Codecs?.OfType<TagLib.ICodec>().Select(c => c.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "";

                return new AudioInfo
                {
                    Year = file.Tag.Year > 0 ? file.Tag.Year : track.Year,
                    Format = track.Format,
                    Codec = codec,
                    DurationSeconds = props?.Duration.TotalSeconds ?? track.DurationSeconds,
                    SampleRate = props?.AudioSampleRate ?? 0,
                    // MP3 / AAC など非可逆形式はビット深度という概念が無く 0 が返る（そのときは表示しない）
                    BitsPerSample = props?.BitsPerSample ?? 0,
                    Channels = props?.AudioChannels ?? 0,
                    BitrateKbps = props?.AudioBitrate ?? 0,
                    FileSize = size,
                    IsLossless = IsLossless(path, codec)
                };
            }
            catch
            {
                // 読めないファイルでもライブラリの情報で分かる範囲は出す
                return new AudioInfo
                {
                    Year = track.Year,
                    Format = track.Format,
                    DurationSeconds = track.DurationSeconds,
                    FileSize = size,
                    IsLossless = IsLossless(path, "")
                };
            }
        }

        /// <summary>
        /// ロスレス（元の音をそのまま持っている）か。FLAC / WAV / AIFF は常にロスレス。
        /// M4A は中身が ALAC（Apple Lossless）ならロスレス、AAC なら非可逆なので、コーデック名で見分ける。
        /// </summary>
        private static bool IsLossless(string path, string codec)
        {
            string ext = Path.GetExtension(path);
            if (LosslessExtensions.Contains(ext))
                return true;
            return codec.Contains("lossless", StringComparison.OrdinalIgnoreCase)
                || codec.Contains("alac", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 表示用の行に整形する。例:
        /// 「2019 · FLAC · 4:32」「44.1 kHz · 24 bit · ステレオ」「1,024 kbps · 34.5 MB」「ロスレス」
        /// </summary>
        public static IReadOnlyList<string> FormatLines(AudioInfo info)
        {
            var lines = new List<string>();

            var first = new List<string>();
            if (info.Year > 0) first.Add(info.Year.ToString());
            first.Add(info.Format);
            first.Add(Track.FormatDuration(info.DurationSeconds));
            lines.Add(string.Join(" · ", first));

            var second = new List<string>();
            // 44100 → 「44.1 kHz」、48000 → 「48 kHz」（末尾の .0 は付けない）
            if (info.SampleRate > 0) second.Add($"{info.SampleRate / 1000.0:0.###} kHz");
            if (info.BitsPerSample > 0) second.Add($"{info.BitsPerSample} bit");
            if (info.Channels > 0) second.Add(info.Channels switch
            {
                1 => Strings.Get("AudioInfo_Mono"),
                2 => Strings.Get("AudioInfo_Stereo"),
                _ => $"{info.Channels} ch"
            });
            if (second.Count > 0)
                lines.Add(string.Join(" · ", second));

            var third = new List<string>();
            if (info.BitrateKbps > 0) third.Add($"{info.BitrateKbps:N0} kbps");
            if (info.FileSize > 0) third.Add(FormatSize(info.FileSize));
            if (third.Count > 0)
                lines.Add(string.Join(" · ", third));

            lines.Add(Strings.Get(info.IsLossless ? "AudioInfo_Lossless" : "AudioInfo_Lossy"));
            return lines;
        }

        /// <summary>バイト数を「34.5 MB」のように 1024 単位で整形する（エクスプローラーの表示に合わせる）。</summary>
        private static string FormatSize(long bytes)
        {
            const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
            return bytes >= Gb ? $"{bytes / Gb:0.00} GB"
                 : bytes >= Mb ? $"{bytes / Mb:0.0} MB"
                 : $"{bytes / Kb:0} KB";
        }
    }
}
