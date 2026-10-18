using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Services;

namespace MusicPlayer.Models
{
    /// <summary>
    /// アルバム 1 枚分。同じ曲が FLAC / MP3 / WAV で重複しているライブラリのため、
    /// 形式（Format）もキーに含め「形式ごとに別アルバム」として扱う。
    /// </summary>
    public sealed class Album : INotifyPropertyChanged
    {
        public Album(string key, string title, string artist, string format, List<Track> tracks)
        {
            Key = key;
            Title = title;
            Artist = artist;
            Format = format;
            // ディスク番号 → トラック番号 → ファイル名 の順に並べる（タグが無い曲はファイル名順になる）
            Tracks = tracks
                .OrderBy(t => t.DiscNumber)
                .ThenBy(t => t.TrackNumber == 0 ? uint.MaxValue : t.TrackNumber)
                .ThenBy(t => t.Path, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            Year = Tracks.Select(t => t.Year).Where(y => y > 0).DefaultIfEmpty(0u).Max();
            // 追加順はアルバム内で一番新しく置かれた曲の日時（曲を足したアルバムが上に来る）
            AddedTicks = Tracks.Select(t => t.AddedTicks).DefaultIfEmpty(0).Max();
        }

        /// <summary>並べ替え用のアルバム名の読み（LibraryService が設定する）。</summary>
        public string TitleReading { get; set; } = "";

        /// <summary>並べ替え用のアーティスト名の読み。</summary>
        public string ArtistReading { get; set; } = "";

        /// <summary>追加順の並べ替えに使う日時（UTC Ticks）。</summary>
        public long AddedTicks { get; }

        /// <summary>アルバムを一意に識別するキー（アーティスト|アルバム|形式）。</summary>
        public string Key { get; }
        public string Title { get; }
        public string Artist { get; }
        public string Format { get; }
        public uint Year { get; }
        public List<Track> Tracks { get; }

        /// <summary>ジャケットのキャッシュキー（先頭曲のフォルダ。再生中の曲からも同じキーで引ける）。</summary>
        public string ArtworkKey => ArtworkCache.KeyFor(Tracks.Count > 0 ? Tracks[0].Path : Key);

        public string YearText => Year > 0 ? Year.ToString() : "";

        /// <summary>「12 曲 · 54:10」のような曲数と合計時間。</summary>
        public string SummaryText =>
            Strings.Format("Album_Summary", Tracks.Count, Track.FormatDuration(Tracks.Sum(t => t.DurationSeconds)));

        private ImageSource? _cover;
        private bool _coverRequested;

        /// <summary>ジャケット画像。最初は null で、EnsureCoverAsync が読み込むと通知される。</summary>
        public ImageSource? Cover
        {
            get => _cover;
            private set
            {
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            }
        }

        /// <summary>
        /// 表示されたタイミングで呼ぶ（GridView の ContainerContentChanging など）。
        /// 画面外のアルバムまで一斉に読まないことで、起動とスクロールを軽く保つ。
        /// UI スレッドから呼ぶこと（BitmapImage は UI スレッドでしか作れない）。
        /// </summary>
        public async Task EnsureCoverAsync()
        {
            if (_coverRequested || Tracks.Count == 0)
                return;

            _coverRequested = true;
            string? path = await ArtworkCache.GetOrCreateAsync(ArtworkKey, Tracks[0].Path);
            if (path != null)
                Cover = new BitmapImage(new System.Uri(path)) { DecodePixelWidth = (int)AppConfig.ArtworkCacheSize };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
