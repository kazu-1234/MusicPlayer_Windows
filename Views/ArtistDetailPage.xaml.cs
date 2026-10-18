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
    /// <summary>アーティスト詳細（アルバム一覧 / 曲一覧）。ライブラリの形式フィルタを引き継ぐ。</summary>
    public sealed partial class ArtistDetailPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;
        private List<Track> _tracks = new();
        private List<Album> _albums = new();

        /// <summary>表示中のアーティスト（同じアーティストへのリンクを押したときに遷移し直さないための判定用）。</summary>
        public Artist? CurrentArtist { get; private set; }

        /// <summary>
        /// アーティストごとに、最後に「曲」タブを見ていたか。ページは開くたびに作り直されるので static に持ち、
        /// 「戻る」で帰ってきたときに離れたときのタブ（とスクロール位置）に戻すのに使う。
        /// </summary>
        private static readonly Dictionary<Artist, bool> LastSongsTab = new();

        private static readonly SortKey[] AlbumKeys = { SortKey.Name, SortKey.Year, SortKey.Added };

        /// <summary>曲一覧の見出し（押して並べ替え）。並べ替えはライブラリの「曲」と共通の設定を使う。</summary>
        private readonly SortHeader _songsHeader;

        public ArtistDetailPage()
        {
            InitializeComponent();
            TrackMenu.Attach(SongsList, this, () => _tracks, o => o as Track);
            _songsHeader = new SortHeader(SongsHeader, () => _state.Settings.SongsSort, s =>
            {
                _state.Settings.SongsSort = s;
                _state.Settings.Save();
                ApplySort();
                ListScroll.ToTop(SongsList); // 並べ替えたら一番上から見せる
            });
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is not Artist artist)
                return;
            CurrentArtist = artist;

            // ライブラリで形式を絞っていれば、ここでも同じ形式だけを出す（FLAC と MP3 の重複を避ける）
            // （形式が 1 種類しか無いときはフィルタを隠しているので絞らない）
            string format = _state.Library.Formats.Count >= 2 ? _state.Settings.LibraryFormatFilter : "";
            _tracks = artist.Tracks.Where(t => format.Length == 0 || t.Format == format).ToList();
            _albums = artist.Albums.Where(a => format.Length == 0 || a.Format == format).ToList();

            NameText.Text = artist.Name;
            InitialText.Text = artist.Initial;
            InfoText.Text = Strings.Format("Artist_Summary", _albums.Count, _tracks.Count);
            // 戻る／進むで来たときは、離れたときのタブにする（新しく開いたときは既定のアルバム）
            if (e.NavigationMode is NavigationMode.Back or NavigationMode.Forward
                && LastSongsTab.TryGetValue(artist, out bool songs) && songs)
                ViewSelector.SelectedItem = SongsTab;
            AttachSortMenu();
            ApplySort();
            // アルバムを開いて「戻る」で帰ってきたら、開く前のスクロール位置に戻す
            ScrollMemory.Restore(this, e, artist, ShowingSongs ? "Songs" : "Albums", ShowingSongs ? SongsList : AlbumsGrid);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (CurrentArtist == null)
                return;
            LastSongsTab[CurrentArtist] = ShowingSongs;
            ScrollMemory.Save(this, CurrentArtist, ShowingSongs ? "Songs" : "Albums", ShowingSongs ? SongsList : AlbumsGrid);
        }

        private bool ShowingSongs => ViewSelector.SelectedItem == SongsTab;

        /// <summary>
        /// 上部の並べ替えボタンはアルバム用（曲は見出しを押して並べ替える）。曲を表示中は隠す。
        /// </summary>
        private void AttachSortMenu()
        {
            var settings = _state.Settings;
            SortButton.Visibility = ShowingSongs ? Visibility.Collapsed : Visibility.Visible;
            SortMenu.Attach(SortButton, AlbumKeys, false, () => settings.AlbumsSort, s => { settings.AlbumsSort = s; settings.Save(); ApplySort(); });
        }

        private void ApplySort()
        {
            var settings = _state.Settings;
            _tracks = SortService.Sort(_tracks, settings.SongsSort);
            _songsHeader.Refresh();
            var albumSort = AlbumKeys.Contains(settings.AlbumsSort.Key) ? settings.AlbumsSort : new SortSpec { Descending = settings.AlbumsSort.Descending, NameMode = settings.AlbumsSort.NameMode };
            var albums = SortService.Sort(_albums, albumSort);
            // ライブラリと同じく、頭文字（年順なら年）ごとの見出しで区切る
            AlbumsGrid.ItemsSource = LibraryPage.Grouped(SortService.GroupConsecutive(albums, a => SortService.HeaderOf(a, albumSort)));
            SongsList.ItemsSource = _tracks;
        }

        private void ViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            // XAML 読み込み中（IsSelected の初期化時）は一覧がまだ作られていない
            if (SongsList == null || AlbumsGrid == null)
                return;

            bool songs = sender.SelectedItem == SongsTab;
            SongsPanel.Visibility = songs ? Visibility.Visible : Visibility.Collapsed;
            AlbumsGrid.Visibility = songs ? Visibility.Collapsed : Visibility.Visible;
            if (SortButton != null)
                AttachSortMenu();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e) =>
            _state.Playback.PlayTracks(_tracks, 0, shuffle: false);

        private void ShuffleButton_Click(object sender, RoutedEventArgs e) =>
            _state.Playback.PlayTracks(_tracks, 0, shuffle: true);

        private void AlbumsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Album album)
                Frame.Navigate(typeof(AlbumDetailPage), album);
        }

        private void AlbumsGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue && args.Item is Album album)
                _ = album.EnsureCoverAsync();
        }

        private void SongsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Track track)
                _state.Playback.PlayTracks(_tracks, _tracks.IndexOf(track));
        }

        // ───────── アルバム名のリンク ─────────

        /// <summary>行のアルバム名を押したら、そのアルバムのページへ。</summary>
        private void AlbumLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Track item)
                LibraryNavigation.GoToAlbum(Frame, item);
        }
    }
}
