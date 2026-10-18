using System.Collections.Generic;
using System.Text;

namespace MusicPlayer.Models
{
    /// <summary>プレイリスト一覧に並べる 1 件（中身は開くまで読まない）。</summary>
    public sealed class PlaylistInfo
    {
        public PlaylistInfo(string name, string path, int count)
        {
            Name = name;
            Path = path;
            Count = count;
        }

        public string Name { get; }
        public string Path { get; }
        public int Count { get; }
        public string CountText => Strings.Format("Playlist_CountFormat", Count);
    }

    /// <summary>
    /// m3u の 1 曲分。既存エントリは元の行（パス文字列・#EXTINF 行）をそのまま保持し、
    /// 保存時に書き戻すことで、手を付けていない行が勝手に変わらないようにする。
    /// </summary>
    public sealed class PlaylistEntry
    {
        public PlaylistEntry(string rawPath, string? extInfLine, Track track, bool playable)
        {
            RawPath = rawPath;
            ExtInfLine = extInfLine;
            Track = track;
            IsMissing = !playable;
        }

        /// <summary>m3u に書かれていたパス文字列（相対パスならそのまま）。</summary>
        public string RawPath { get; }

        /// <summary>直前の #EXTINF 行（無ければ null。新規追加の曲は保存時に生成する）。</summary>
        public string? ExtInfLine { get; }

        /// <summary>表示・再生用の曲情報（絶対パス）。</summary>
        public Track Track { get; }

        /// <summary>
        /// 再生できない（一覧で薄く表示し、再生の対象から外す）。
        /// 書かれたパスにファイルが無いとき、またはファイルはあってもライブラリに入っていないとき。
        /// </summary>
        public bool IsMissing { get; }

        public double RowOpacity => IsMissing ? 0.45 : 1.0;
    }

    /// <summary>読み込んだプレイリストの中身と、保存時に元の形式を再現するための情報。</summary>
    public sealed class PlaylistDocument
    {
        public string Path { get; set; } = "";
        public List<PlaylistEntry> Entries { get; set; } = new();

        /// <summary>元ファイルの文字コード（UTF-8 BOM なし / BOM あり / Shift_JIS など）。</summary>
        public Encoding Encoding { get; set; } = new UTF8Encoding(false);

        /// <summary>元ファイルの改行コード（既存ファイルは CRLF のことが多い）。</summary>
        public string NewLine { get; set; } = "\r\n";

        /// <summary>先頭に #EXTM3U があったか（新規作成時は付ける）。</summary>
        public bool HasHeader { get; set; } = true;
    }
}
