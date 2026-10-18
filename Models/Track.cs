using System;
using System.IO;
using Newtonsoft.Json;

namespace MusicPlayer.Models
{
    /// <summary>
    /// 1 曲分の情報。ライブラリキャッシュ（library.json）にこのまま保存される。
    /// タグが空のときは表示用プロパティ（Display*）でフォルダ名などから補う。
    /// </summary>
    public sealed class Track
    {
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string AlbumArtist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public uint TrackNumber { get; set; }
        public uint DiscNumber { get; set; }
        public uint Year { get; set; }
        public double DurationSeconds { get; set; }

        /// <summary>差分スキャン用：前回読んだときのファイル更新日時（UTC Ticks）。</summary>
        public long LastWriteTicks { get; set; }

        /// <summary>差分スキャン用：前回読んだときのファイルサイズ。</summary>
        public long FileSize { get; set; }

        /// <summary>「追加順」用：ファイルの作成日時（UTC Ticks）。フォルダに置かれた日時。</summary>
        public long AddedTicks { get; set; }

        // ───────── 名前順の並べ替え用の読み（キャッシュに保存する） ─────────
        // タグのソート用項目（TITLESORT など）があればそれ、無ければ Windows の辞書で漢字をかなにした読み。
        // スキャン時に 1 回だけ作り、表示のたびに計算しない。

        /// <summary>タグに書かれた曲名の読み（無ければ空）。</summary>
        public string TitleSort { get; set; } = "";

        /// <summary>タグに書かれたアーティストの読み（無ければ空）。</summary>
        public string ArtistSort { get; set; } = "";

        /// <summary>タグに書かれたアルバムの読み（無ければ空）。</summary>
        public string AlbumSort { get; set; } = "";

        /// <summary>並べ替えに使う曲名の読み（ReadingService が作る）。</summary>
        public string TitleReading { get; set; } = "";

        /// <summary>並べ替えに使うアーティストの読み。</summary>
        public string ArtistReading { get; set; } = "";

        /// <summary>並べ替えに使うアルバムの読み。</summary>
        public string AlbumReading { get; set; } = "";

        // ───────── 表示用（キャッシュには保存しない） ─────────

        /// <summary>形式名（FLAC / MP3 / WAV など）。形式フィルタとアルバムの区別に使う。</summary>
        [JsonIgnore]
        public string Format => FormatFromPath(Path);

        [JsonIgnore]
        public string DisplayTitle =>
            string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title;

        [JsonIgnore]
        public string DisplayArtist =>
            string.IsNullOrWhiteSpace(Artist) ? Strings.Get("Common_UnknownArtist") : Artist;

        /// <summary>
        /// アーティスト一覧でまとめる主のアーティスト（「AI Feat. カンナ」なら AI）。
        /// ライブラリを組み立てるとき（LibraryService.BuildIndexes）にアルバム単位で決める。曲の表示はファイルどおりの表記のまま。
        /// </summary>
        [JsonIgnore]
        public string PrimaryArtist { get; set; } = "";

        [JsonIgnore]
        public string DisplayAlbum =>
            string.IsNullOrWhiteSpace(Album) ? Strings.Get("Common_UnknownAlbum") : Album;

        /// <summary>アルバムのまとめ役となるアーティスト（アルバムアーティスト優先）。</summary>
        [JsonIgnore]
        public string EffectiveAlbumArtist =>
            !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : Artist;

        [JsonIgnore]
        public string DurationText => FormatDuration(DurationSeconds);

        /// <summary>曲一覧の「追加日」列（ファイルの作成日をローカル時刻の日付で）。</summary>
        [JsonIgnore]
        public string AddedText => AddedTicks <= 0
            ? ""
            : new DateTime(AddedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd");

        /// <summary>一覧の左端に出すトラック番号（0 なら空）。</summary>
        [JsonIgnore]
        public string TrackNumberText => TrackNumber > 0 ? TrackNumber.ToString() : "";

        public static string FormatFromPath(string path)
        {
            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            return ext.Length == 0 ? "?" : ext;
        }

        /// <summary>秒数を「m:ss」または「h:mm:ss」に整形する。</summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds))
                return "0:00";

            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>ファイルが存在するか（プレイリストの「見つからない曲」表示に使う）。</summary>
        [JsonIgnore]
        public bool Exists => File.Exists(Path);
    }
}
