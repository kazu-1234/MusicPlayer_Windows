using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MusicPlayer.Native
{
    /// <summary>CD の 1 トラック分の目次情報。</summary>
    public sealed class CdTocEntry
    {
        public int Number { get; init; }

        /// <summary>開始位置（LBA = 論理ブロック番号。1 セクタ = 1/75 秒）。</summary>
        public int StartLba { get; init; }

        /// <summary>音楽トラックか（false ならデータトラック。エンハンスド CD の動画など）。</summary>
        public bool IsAudio { get; init; }
    }

    /// <summary>CD の目次（TOC）。</summary>
    public sealed class CdToc
    {
        public List<CdTocEntry> Tracks { get; } = new();

        /// <summary>ディスクの終端（リードアウト）の LBA。</summary>
        public int LeadOutLba { get; set; }
    }

    /// <summary>
    /// Win32 の DeviceIoControl で CD ドライブを直接操作する。
    /// Windows には CD から音楽を読み出す API が無いため、ドライブへ制御コード（IOCTL）を送って
    /// 目次（TOC）と生のオーディオデータ（1 セクタ 2352 バイト）を読む。
    /// </summary>
    public sealed class CdRomIoctl : IDisposable
    {
        /// <summary>CD-DA の 1 セクタのバイト数（44.1kHz × 16bit × 2ch ÷ 75 セクタ/秒）。</summary>
        public const int RawSectorSize = 2352;

        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
        private const uint OpenExisting = 3;

        private const uint IoctlCdromReadToc = 0x00024000;
        private const uint IoctlCdromRawRead = 0x0002403E;
        private const uint IoctlStorageEjectMedia = 0x002D4808;

        /// <summary>RAW_READ_INFO の TrackMode: CDDA（音楽 CD）。</summary>
        private const int TrackModeCdda = 2;

        /// <summary>TOC の制御フィールドで「データトラック」を表すビット。</summary>
        private const byte ControlDataTrack = 0x04;

        /// <summary>リードアウトを表す特別なトラック番号。</summary>
        private const byte LeadOutTrackNumber = 0xAA;

        private readonly SafeFileHandle _handle;

        /// <param name="driveLetter">ドライブ文字（例: 'E'）。</param>
        public CdRomIoctl(char driveLetter)
        {
            // \\.\E: という名前でドライブそのもの（ファイルシステムではなくデバイス）を開く
            _handle = CreateFile($@"\\.\{driveLetter}:", GenericRead, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (_handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>目次（各トラックの開始位置）を読む。</summary>
        public CdToc ReadToc()
        {
            // CDROM_TOC 構造体: 長さ(2) + 最初/最後のトラック番号(1+1) + TRACK_DATA(8 バイト) × 100
            const int tocSize = 4 + 100 * 8;
            IntPtr buffer = Marshal.AllocHGlobal(tocSize);
            try
            {
                if (!DeviceIoControl(_handle, IoctlCdromReadToc, IntPtr.Zero, 0, buffer, tocSize, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                int first = Marshal.ReadByte(buffer, 2);
                int last = Marshal.ReadByte(buffer, 3);
                var toc = new CdToc();

                // トラック数 + リードアウト 1 件分を読む
                for (int i = 0; i <= last - first + 1; i++)
                {
                    int offset = 4 + i * 8;
                    // TRACK_DATA: Reserved(1) / Control:4bit + Adr:4bit(1) / TrackNumber(1) / Reserved(1) / Address[4]
                    byte controlAdr = Marshal.ReadByte(buffer, offset + 1);
                    byte number = Marshal.ReadByte(buffer, offset + 2);
                    // Address は MSF（分・秒・フレーム）形式: [0, M, S, F]
                    int m = Marshal.ReadByte(buffer, offset + 5);
                    int s = Marshal.ReadByte(buffer, offset + 6);
                    int f = Marshal.ReadByte(buffer, offset + 7);
                    // MSF → LBA。CD の先頭には 2 秒（150 フレーム）のプリギャップがあるので引く
                    int lba = (m * 60 + s) * 75 + f - 150;

                    if (number == LeadOutTrackNumber)
                    {
                        toc.LeadOutLba = lba;
                        break;
                    }

                    // MSVC のビットフィールドは下位ビットから詰めるので、先に宣言された Control が下位 4 ビット
                    byte control = (byte)(controlAdr & 0x0F);
                    toc.Tracks.Add(new CdTocEntry
                    {
                        Number = number,
                        StartLba = lba,
                        IsAudio = (control & ControlDataTrack) == 0
                    });
                }

                return toc;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// lba から sectorCount セクタ分の生オーディオ（16bit リトルエンディアン・ステレオの PCM）を読む。
        /// </summary>
        public bool TryReadSectors(int lba, int sectorCount, byte[] destination)
        {
            // RAW_READ_INFO: DiskOffset(8, 2048 バイト単位のオフセット) / SectorCount(4) / TrackMode(4)
            IntPtr info = Marshal.AllocHGlobal(16);
            IntPtr output = Marshal.AllocHGlobal(sectorCount * RawSectorSize);
            try
            {
                // DiskOffset はなぜか「2048 バイトセクタ換算」で指定する仕様（Windows の CD ドライバの決まり）
                Marshal.WriteInt64(info, 0, (long)lba * 2048);
                Marshal.WriteInt32(info, 8, sectorCount);
                Marshal.WriteInt32(info, 12, TrackModeCdda);

                bool ok = DeviceIoControl(_handle, IoctlCdromRawRead, info, 16, output,
                    (uint)(sectorCount * RawSectorSize), out uint returned, IntPtr.Zero);
                if (!ok || returned < sectorCount * RawSectorSize)
                    return false;

                Marshal.Copy(output, destination, 0, sectorCount * RawSectorSize);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(output);
                Marshal.FreeHGlobal(info);
            }
        }

        /// <summary>トレイを開く。</summary>
        public void Eject()
        {
            DeviceIoControl(_handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
    }
}
