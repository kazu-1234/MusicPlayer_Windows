using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Native;

namespace MusicPlayer.Services
{
    /// <summary>読み込んだ CD の情報（音楽トラックと MusicBrainz 用 Disc ID）。</summary>
    public sealed class CdDisc
    {
        public char Drive { get; init; }
        public List<CdTocEntry> AudioTracks { get; } = new();

        /// <summary>各音楽トラックの終了 LBA（次のトラックの開始位置、またはディスク終端）。</summary>
        public List<int> TrackEndLba { get; } = new();

        public string DiscId { get; set; } = "";

        /// <summary>MusicBrainz の曖昧検索用 TOC 文字列（「最初 最後 終端 開始位置…」を + でつないだもの）。</summary>
        public string TocQuery { get; set; } = "";

        public double TrackSeconds(int index) => (TrackEndLba[index] - AudioTracks[index].StartLba) / 75.0;
    }

    /// <summary>
    /// CD 取り込み。CdRomIoctl で生データを読み、WAV（44.1kHz / 16bit / ステレオ）として書き出す。
    /// FLAC / MP3 / AAC への変換は呼び出し側で TranscodeService に渡す。
    /// </summary>
    public static class CdRipService
    {
        /// <summary>
        /// エンハンスド CD（音楽 + データ）では、データトラックの直前に 11400 セクタ（152 秒）の
        /// 「セッション間の隙間」があるため、最後の音楽トラックの終端はその分手前になる。
        /// </summary>
        private const int SessionGapSectors = 11400;

        /// <summary>LBA と MusicBrainz の「オフセット」の差（先頭 2 秒のプリギャップ）。</summary>
        private const int PregapSectors = 150;

        public static IReadOnlyList<char> GetDrives() =>
            DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.CDRom)
                .Select(d => char.ToUpperInvariant(d.Name[0]))
                .ToList();

        /// <summary>CD の目次を読み、音楽トラックと Disc ID を求める。</summary>
        public static Task<CdDisc> ReadDiscAsync(char drive) => Task.Run(() =>
        {
            using var cd = new CdRomIoctl(drive);
            CdToc toc = cd.ReadToc();
            var disc = new CdDisc { Drive = drive };

            for (int i = 0; i < toc.Tracks.Count; i++)
            {
                var t = toc.Tracks[i];
                if (!t.IsAudio)
                    continue;

                int end;
                if (i + 1 < toc.Tracks.Count)
                {
                    var next = toc.Tracks[i + 1];
                    end = next.IsAudio ? next.StartLba : next.StartLba - SessionGapSectors;
                }
                else
                {
                    end = toc.LeadOutLba;
                }

                disc.AudioTracks.Add(t);
                disc.TrackEndLba.Add(end);
            }

            if (disc.AudioTracks.Count == 0)
                throw new InvalidOperationException(Strings.Get("Rip_NoAudioTracks"));

            ComputeDiscId(disc);
            return disc;
        });

        /// <summary>
        /// MusicBrainz Disc ID を計算する。
        /// 「最初の番号(2桁16進) + 最後の番号(2桁) + 終端と各トラックのオフセット(8桁×100)」の SHA-1 を、
        /// URL で使える文字（+ → . / / → _ / = → -）に置き換えた Base64 にしたもの。
        /// </summary>
        private static void ComputeDiscId(CdDisc disc)
        {
            int first = disc.AudioTracks[0].Number;
            int last = disc.AudioTracks[^1].Number;
            int leadOut = disc.TrackEndLba[^1] + PregapSectors;

            var offsets = new int[100];
            offsets[0] = leadOut;
            foreach (var t in disc.AudioTracks)
                offsets[t.Number] = t.StartLba + PregapSectors;

            var sb = new StringBuilder();
            sb.Append(first.ToString("X2")).Append(last.ToString("X2"));
            foreach (int o in offsets)
                sb.Append(o.ToString("X8"));

            byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
            disc.DiscId = Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');

            var toc = new List<int> { first, last, leadOut };
            toc.AddRange(disc.AudioTracks.Select(t => t.StartLba + PregapSectors));
            disc.TocQuery = string.Join("+", toc);
        }

        /// <summary>
        /// 1 トラックを WAV に書き出す。読み取りに失敗した部分は再試行し、それでもダメなら無音で埋める。
        /// </summary>
        /// <returns>読めずに無音で埋めたセクタ数（0 なら完全に読めた）。</returns>
        public static Task<int> RipTrackToWavAsync(CdDisc disc, int index, string wavPath, IProgress<double>? progress, CancellationToken token) =>
            Task.Run(() =>
            {
                int start = disc.AudioTracks[index].StartLba;
                int end = disc.TrackEndLba[index];
                int total = end - start;
                int failedSectors = 0;

                using var cd = new CdRomIoctl(disc.Drive);
                using var file = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                WriteWavHeader(file, (long)total * CdRomIoctl.RawSectorSize);

                var buffer = new byte[AppConfig.CdSectorsPerRead * CdRomIoctl.RawSectorSize];
                for (int lba = start; lba < end;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(AppConfig.CdSectorsPerRead, end - lba);

                    if (ReadWithRetry(cd, lba, count, buffer))
                    {
                        file.Write(buffer, 0, count * CdRomIoctl.RawSectorSize);
                    }
                    else
                    {
                        // まとめて読めないときは 1 セクタずつ読み、傷のある所だけを無音にする
                        var one = new byte[CdRomIoctl.RawSectorSize];
                        for (int i = 0; i < count; i++)
                        {
                            if (!ReadWithRetry(cd, lba + i, 1, one))
                            {
                                Array.Clear(one);
                                failedSectors++;
                            }
                            file.Write(one, 0, one.Length);
                        }
                    }

                    lba += count;
                    progress?.Report((double)(lba - start) / total);
                }

                return failedSectors;
            }, token);

        public static void Eject(char drive)
        {
            using var cd = new CdRomIoctl(drive);
            cd.Eject();
        }

        private static bool ReadWithRetry(CdRomIoctl cd, int lba, int count, byte[] buffer)
        {
            for (int attempt = 0; attempt <= AppConfig.CdReadRetries; attempt++)
            {
                if (cd.TryReadSectors(lba, count, buffer))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 44 バイトの WAV ヘッダ（RIFF / fmt / data）を書く。
        /// CD の音声は 44100Hz・16bit・2ch の非圧縮 PCM なので、そのまま data に並べれば WAV になる。
        /// </summary>
        private static void WriteWavHeader(Stream stream, long dataBytes)
        {
            const int sampleRate = 44100, channels = 2, bitsPerSample = 16;
            const int blockAlign = channels * bitsPerSample / 8;        // 1 サンプル（左右）= 4 バイト
            const int byteRate = sampleRate * blockAlign;               // 1 秒 = 176,400 バイト

            using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write((uint)(36 + dataBytes));     // 以降のファイルサイズ
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                          // fmt チャンクの長さ
            w.Write((short)1);                    // 1 = リニア PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write((short)blockAlign);
            w.Write((short)bitsPerSample);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write((uint)dataBytes);
        }
    }
}
