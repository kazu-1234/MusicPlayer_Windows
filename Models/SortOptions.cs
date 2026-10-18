namespace MusicPlayer.Models
{
    /// <summary>一覧の並べ替えの基準。</summary>
    public enum SortKey
    {
        /// <summary>名前（曲名・アルバム名・アーティスト名）。</summary>
        Name,
        Artist,
        Album,
        Year,
        /// <summary>追加順（ファイルの作成日時）。</summary>
        Added,
        /// <summary>形式（FLAC / MP3 など）。曲一覧の見出しから選ぶ。</summary>
        Format,
        /// <summary>曲の長さ。</summary>
        Duration,
        /// <summary>ディスク番号 → トラック番号（アルバム詳細の既定の並び）。</summary>
        TrackNumber
    }

    /// <summary>名前の並べ方（設定）。</summary>
    public enum NameSortMode
    {
        /// <summary>英字・かな・漢字をグループに分ける（従来どおり）。漢字の中は読み順。</summary>
        Separate,
        /// <summary>漢字も読みに直して、かなと一緒に 50 音順に混ぜる。</summary>
        Mixed
    }

    /// <summary>並べ替えの基準と向き（設定に保存する）。</summary>
    public sealed class SortSpec
    {
        public SortKey Key { get; set; } = SortKey.Name;

        /// <summary>true なら降順（大きい順・新しい順・ん→あ）。</summary>
        public bool Descending { get; set; }

        /// <summary>
        /// この並べ替えをしたときの名前の並べ方（分ける／混ぜる）。
        /// 設定を切り替えただけでは今の並びを変えず、次に並べ替えたときに新しい設定を使うため、並べ替えごとに覚えておく。
        /// null は「今の設定に従う」（古い設定ファイルとの互換用。読み込み時に今の設定で埋める）。
        /// </summary>
        public NameSortMode? NameMode { get; set; }

        public SortSpec Clone() => new() { Key = Key, Descending = Descending, NameMode = NameMode };
    }
}
