using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.Generic;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Views
{
    /// <summary>アルバム詳細（曲一覧・全曲再生・シャッフル）。</summary>
    public sealed partial class AlbumDetailPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;
        private Album? _album;

        /// <summary>表示中の曲の並び（見出しで並べ替えた結果）。再生もこの順で行う。</summary>
        private List<Track> _tracks = new();

        /// <summary>
        /// 見出しでの並べ替え。表示だけを変え、設定には残さない（開くたびにトラック番号順から始める）。
        /// </summary>
        private SortSpec _sort = new() { Key = SortKey.TrackNumber };

        private readonly SortHeader _header;

        public AlbumDetailPage()
        {
            InitializeComponent();
            _header = new SortHeader(TracksHeader, () => _sort, s =>
            {
                _sort = s;
                ShowTracks();
                ListScroll.ToTop(TracksList); // 並べ替えたら一番上から見せる
            });
            ArtHost.Width = ArtHost.Height = AppConfig.DetailArtSize;
            TrackMenu.Attach(TracksList, this, () => _tracks.Cast<object>().ToList(), o => o as Track);
            A11y.Label(MoreButton, Strings.Get("Detail_More"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e) => SafeAsync.Run(() => OnNavigatedToAsync(e), XamlRoot, "OnNavigatedTo");

        private async Task OnNavigatedToAsync(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _album = e.Parameter as Album;
            if (_album == null)
                return;

            TitleText.Text = _album.Title;
            ArtistText.Text = _album.Artist;
            InfoText.Text = string.Join(" · ", new[] { _album.YearText, _album.Format, _album.SummaryText }.Where(s => s.Length > 0));
            _sort = new SortSpec { Key = SortKey.TrackNumber };
            ShowTracks();
            // 「…」ボタンはアルバム全曲に対する右クリックメニューと同じ内容
            MoreButton.Flyout = TrackMenu.Create(this, () => _album.Tracks);

            await _album.EnsureCoverAsync();
            ArtImage.Source = _album.Cover;
        }

        /// <summary>今の並べ替えで曲一覧を表示し直す。</summary>
        private void ShowTracks()
        {
            if (_album == null)
                return;
            _tracks = SortService.Sort(_album.Tracks, _sort);
            TracksList.ItemsSource = _tracks;
            _header.Refresh();
        }

        // 再生は画面に並んでいる順で行う（並べ替えたらその順に再生される）
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tracks.Count > 0)
                _state.Playback.PlayTracks(_tracks, 0, shuffle: false);
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tracks.Count > 0)
                _state.Playback.PlayTracks(_tracks, 0, shuffle: true);
        }

        private void TracksList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
                _state.Playback.PlayTracks(_tracks, _tracks.IndexOf(track));
        }
    }
}
