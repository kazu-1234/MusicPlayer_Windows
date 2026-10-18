using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Views
{
    /// <summary>音楽ライブラリ（曲 / アルバム / アーティスト）。形式フィルタと検索で絞り込む。</summary>
    public sealed partial class LibraryPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;
        private bool _initializing;

        // 現在の絞り込み結果（右クリックの「再生」で一覧の続きを再生するのに使う）
        private List<Track> _visibleTracks = new();

        public LibraryPage()
        {
            InitializeComponent();
            A11y.Label(RescanButton, Strings.Get("Library_Rescan"));
            TrackMenu.Attach(SongsList, this, () => _visibleTracks, o => o as Track);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _state.Library.Changed += Library_Changed;
            _state.Library.ScanStateChanged += UpdateScanState;

            _initializing = true;
            ViewSelector.SelectedItem = _state.Settings.LibraryView switch
            {
                "Songs" => SongsTab,
                "Artists" => ArtistsTab,
                _ => AlbumsTab
            };
            _initializing = false;

            RefreshFormatCombo();
            AttachSortMenu();
            ApplyFilter();
            UpdateScanState();
        }

        // ───────── 並べ替え ─────────

        /// <summary>一覧ごとに選べる並べ替えの基準。</summary>
        private static readonly SortKey[] AlbumKeys = { SortKey.Name, SortKey.Artist, SortKey.Year, SortKey.Added };
        private static readonly SortKey[] ArtistKeys = { SortKey.Name, SortKey.Added };

        /// <summary>曲一覧の見出し（押して並べ替え）。</summary>
        private SortHeader? _songsHeader;

        /// <summary>
        /// 表示中の一覧に合わせて、上部の並べ替えメニューを作り直す。
        /// 曲は見出しを押して並べ替えるので、曲を表示中は上部のボタンを隠す。
        /// </summary>
        private void AttachSortMenu()
        {
            var settings = _state.Settings;
            _songsHeader ??= new SortHeader(SongsHeader, () => settings.SongsSort, s =>
            {
                settings.SongsSort = s;
                SortChanged();
                ListScroll.ToTop(SongsList); // 並べ替えたら一番上から見せる
            });
            SortButton.Visibility = CurrentView == "Songs" ? Visibility.Collapsed : Visibility.Visible;
            switch (CurrentView)
            {
                case "Songs":
                    break;
                case "Artists":
                    SortMenu.Attach(SortButton, ArtistKeys, false, () => settings.ArtistsSort, s => { settings.ArtistsSort = s; SortChanged(); });
                    break;
                default:
                    SortMenu.Attach(SortButton, AlbumKeys, false, () => settings.AlbumsSort, s => { settings.AlbumsSort = s; SortChanged(); });
                    break;
            }
        }

        private void SortChanged()
        {
            _state.Settings.Save();
            ApplyFilter();
        }

        /// <summary>
        /// 実際に使う形式フィルタ。形式が 1 種類しか無いとき（フィルタを隠しているとき）や、
        /// 保存してある形式がもうライブラリに無いときは「すべて」扱いにする。
        /// </summary>
        private string EffectiveFormat
        {
            get
            {
                string f = _state.Settings.LibraryFormatFilter;
                var formats = _state.Library.Formats;
                return formats.Count >= 2 && formats.Contains(f) ? f : "";
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // キャッシュされたページでも、表示していない間は更新を受けない（無駄な再描画を避ける）
            _state.Library.Changed -= Library_Changed;
            _state.Library.ScanStateChanged -= UpdateScanState;
        }

        private void Library_Changed()
        {
            RefreshFormatCombo();
            ApplyFilter();
        }

        /// <summary>形式フィルタの選択肢（すべて + ライブラリにある形式）を作り直す。</summary>
        private void RefreshFormatCombo()
        {
            _initializing = true;
            FormatCombo.Items.Clear();
            FormatCombo.Items.Add(Strings.Get("Library_AllFormats"));
            foreach (string f in _state.Library.Formats)
                FormatCombo.Items.Add(f);

            int index = _state.Library.Formats.ToList().IndexOf(_state.Settings.LibraryFormatFilter);
            FormatCombo.SelectedIndex = index >= 0 ? index + 1 : 0;
            // 形式が混ざっているとき（2 種類以上）だけ表示する。1 種類なら絞る意味が無いので隠す
            FormatCombo.Visibility = _state.Library.Formats.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
            _initializing = false;
        }

        private string CurrentView => (ViewSelector.SelectedItem as SelectorBarItem)?.Tag as string ?? "Albums";

        /// <summary>形式フィルタと検索語で絞り込み、選択中のビューだけ表示する。</summary>
        private void ApplyFilter()
        {
            string format = EffectiveFormat;
            string query = SearchBox.Text.Trim();
            var settings = _state.Settings;

            bool Match(string s) => s.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            bool TrackMatches(Track t) =>
                (format.Length == 0 || t.Format == format)
                && (query.Length == 0 || Match(t.DisplayTitle) || Match(t.DisplayArtist) || Match(t.DisplayAlbum));

            string view = CurrentView;
            SongsPanel.Visibility = view == "Songs" ? Visibility.Visible : Visibility.Collapsed;
            AlbumsGrid.Visibility = view == "Albums" ? Visibility.Visible : Visibility.Collapsed;
            ArtistsGrid.Visibility = view == "Artists" ? Visibility.Visible : Visibility.Collapsed;

            int count;
            switch (view)
            {
                case "Songs":
                    _visibleTracks = SortService.Sort(_state.Library.Tracks.Where(TrackMatches), settings.SongsSort);
                    _songsHeader?.Refresh();
                    SongsList.ItemsSource = _visibleTracks;
                    count = _visibleTracks.Count;
                    break;
                case "Artists":
                    var artistSort = Supported(settings.ArtistsSort, ArtistKeys);
                    var artists = SortService.Sort(_state.Library.Artists
                        .Where(a => a.Tracks.Any(t => format.Length == 0 || t.Format == format))
                        .Where(a => query.Length == 0 || Match(a.Name)), artistSort);
                    // 並べた順のまま、頭文字（追加日順なら年月）ごとの見出しで区切って表示する
                    ArtistsGrid.ItemsSource = Grouped(SortService.GroupConsecutive(artists, a => SortService.HeaderOf(a, artistSort)));
                    count = artists.Count;
                    break;
                default:
                    var albumSort = Supported(settings.AlbumsSort, AlbumKeys);
                    var albums = SortService.Sort(_state.Library.Albums
                        .Where(a => format.Length == 0 || a.Format == format)
                        .Where(a => query.Length == 0 || Match(a.Title) || Match(a.Artist)), albumSort);
                    AlbumsGrid.ItemsSource = Grouped(SortService.GroupConsecutive(albums, a => SortService.HeaderOf(a, albumSort)));
                    count = albums.Count;
                    break;
            }

            EmptyText.Visibility = count == 0 && !_state.Library.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>見出し付きのグループ一覧を GridView に渡せる形（グループ化した CollectionView）にする。</summary>
        internal static object Grouped<T>(List<ItemGroup<T>> groups) =>
            new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;

        /// <summary>その一覧で選べない基準（例: アーティスト一覧で「年」）が保存されていたら名前順にする。</summary>
        private static SortSpec Supported(SortSpec spec, SortKey[] keys) =>
            keys.Contains(spec.Key) ? spec : new SortSpec { Descending = spec.Descending, NameMode = spec.NameMode };

        private void UpdateScanState()
        {
            var lib = _state.Library;
            ScanPanel.Visibility = lib.IsScanning ? Visibility.Visible : Visibility.Collapsed;
            ScanText.Text = Strings.Format("Library_Scanning", lib.ScanProcessed);
            RescanButton.IsEnabled = !lib.IsScanning;
        }

        private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (_initializing)
                return;

            _state.Settings.LibraryView = CurrentView;
            _state.Settings.Save();
            AttachSortMenu();
            ApplyFilter();
        }

        private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FormatCombo.SelectedIndex < 0)
                return;

            _state.Settings.LibraryFormatFilter = FormatCombo.SelectedIndex > 0 ? (string)FormatCombo.SelectedItem : "";
            _state.Settings.Save();
            ApplyFilter();
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFilter();
        }

        private void RescanButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => RescanButton_ClickAsync(sender, e), XamlRoot, "RescanButton_Click");

        private async Task RescanButton_ClickAsync(object sender, RoutedEventArgs e) => await _state.Library.ScanAsync();

        /// <summary>今表示中の絞り込み結果に含まれる全曲をシャッフル再生する。</summary>
        private void ShuffleAllButton_Click(object sender, RoutedEventArgs e)
        {
            string format = EffectiveFormat;
            var tracks = _state.Library.Tracks.Where(t => format.Length == 0 || t.Format == format).ToList();
            _state.Playback.PlayTracks(tracks, 0, shuffle: true);
        }

        private void SongsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
                _state.Playback.PlayTracks(_visibleTracks, _visibleTracks.IndexOf(track));
        }

        private void AlbumsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Album album)
                Frame.Navigate(typeof(AlbumDetailPage), album);
        }

        /// <summary>画面に入ったタイルのジャケットだけ読み込む（全アルバム分を一度に読まない）。</summary>
        private void AlbumsGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue && args.Item is Album album)
                _ = album.EnsureCoverAsync();
        }

        private void AlbumsGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Album album)
            {
                TrackMenu.Create(this, () => album.Tracks)
                    .ShowAt(AlbumsGrid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(AlbumsGrid) });
                e.Handled = true;
            }
        }

        private void ArtistsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Artist artist)
                Frame.Navigate(typeof(ArtistDetailPage), artist);
        }

        private void ArtistsGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Artist artist)
            {
                TrackMenu.Create(this, () => artist.Tracks)
                    .ShowAt(ArtistsGrid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(ArtistsGrid) });
                e.Handled = true;
            }
        }
    }
}
