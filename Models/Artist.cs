using System.Collections.Generic;
using System.Linq;

namespace MusicPlayer.Models
{
    /// <summary>アーティスト 1 人分（曲のアーティストタグ単位）。</summary>
    public sealed class Artist
    {
        public Artist(string name, List<Album> albums, List<Track> tracks)
        {
            Name = name;
            Albums = albums;
            Tracks = tracks;
            // 追加順はそのアーティストで一番新しく置かれた曲の日時
            AddedTicks = tracks.Count == 0 ? 0 : tracks.Max(t => t.AddedTicks);
        }

        /// <summary>並べ替え用の名前の読み（LibraryService が設定する）。</summary>
        public string Reading { get; set; } = "";

        /// <summary>追加順の並べ替えに使う日時（UTC Ticks）。</summary>
        public long AddedTicks { get; }

        public string Name { get; }
        public List<Album> Albums { get; }
        public List<Track> Tracks { get; }

        /// <summary>頭文字アイコン用の 1 文字。</summary>
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        public string SummaryText => Strings.Format("Artist_Summary", Albums.Count, Tracks.Count);
    }
}
