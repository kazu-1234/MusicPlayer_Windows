using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MusicPlayer.Views;
using Windows.System;
using WinUiShared;

namespace MusicPlayer
{
    public sealed partial class MainWindow : Window, IAppMainWindow
    {
        private const int DefaultClientWidth = 1200;
        private const int DefaultClientHeight = 760;
        private const double MinimumWindowWidth = 900;
        private const double MinimumWindowHeight = 600;

        /// <summary>Nav のタグ → ページの型。</summary>
        private static readonly Dictionary<string, Type> PageTypes = new()
        {
            ["NowPlaying"] = typeof(NowPlayingPage),
            ["Library"] = typeof(LibraryPage),
            ["TagEditor"] = typeof(TagEditorPage),
            ["CdRip"] = typeof(CdRipPage),
            ["Convert"] = typeof(ConvertPage),
            ["Sync"] = typeof(SyncPage),
            ["Info"] = typeof(InfoPage),
            ["Settings"] = typeof(SettingsPage)
        };

        /// <summary>詳細ページは親の Nav 項目を選択状態にする（アルバム詳細 → 音楽ライブラリ など）。</summary>
        private static readonly Dictionary<Type, string> DetailPageParents = new()
        {
            [typeof(AlbumDetailPage)] = "Library",
            [typeof(ArtistDetailPage)] = "Library"
        };

        private readonly AppRuntime _runtime;
        private bool _windowBoundsReady;
        private TitleBarThemeHelper? _titleBarThemeHelper;

        public MainWindow(AppRuntime runtime)
        {
            _runtime = runtime;

            InitializeComponent();
            Title = Strings.Get("AppName");
            AppTitleBar.Title = Strings.Get("AppName");
            ApplyWindowIcon();

            ThemeService.AttachRoot(RootGrid);
            _titleBarThemeHelper = new TitleBarThemeHelper(this, RootGrid, AppTitleBar);

            AppWindow.Closing += AppWindow_Closing;
            AppWindow.Changed += AppWindow_Changed;
            ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;
            ContentFrame.Navigated += ContentFrame_Navigated;
            // 再生バーのアーティスト名・アルバム名を押したら、そのページへ
            PlayerBar.ArtistRequested += track => Views.LibraryNavigation.GoToArtist(ContentFrame, track);
            PlayerBar.AlbumRequested += track => Views.LibraryNavigation.GoToAlbum(ContentFrame, track);

            ConfigureMinimumWindowSize();
            SetUpBackForwardInput();
            SetUpTaskbarButtons();

            // サイドバーのプレイリスト一覧は、作成・名前変更・削除・保存のたびに作り直す
            _runtime.AppState.Playlists.Changed += Playlists_Changed;
            RefreshPlaylistNav();
        }

        // ───────── タスクバーのプレビューのボタン（前へ／再生・一時停止／次へ） ─────────

        private Native.TaskbarThumbButtons? _thumbButtons;

        private void SetUpTaskbarButtons()
        {
            try
            {
                var playback = _runtime.AppState.Playback;
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                _thumbButtons = new Native.TaskbarThumbButtons(hwnd, button =>
                {
                    // ボタンはウィンドウのメッセージ処理（UI スレッド）から呼ばれるので、そのまま再生を操作してよい
                    switch (button)
                    {
                        case Native.TaskbarThumbButtons.Button.Previous: playback.Previous(); break;
                        case Native.TaskbarThumbButtons.Button.PlayPause: playback.PlayPause(); break;
                        case Native.TaskbarThumbButtons.Button.Next: playback.Next(); break;
                    }
                }, Strings.Get("Player_Previous"), Strings.Get("Thumb_Play"), Strings.Get("Thumb_Pause"), Strings.Get("Player_Next"));

                playback.PlaybackStateChanged += UpdateTaskbarButtons;
                playback.QueueChanged += UpdateTaskbarButtons;
                UpdateTaskbarButtons();
            }
            catch (Exception ex)
            {
                // ボタンが出せなくてもアプリ本体の動作には影響させない
                AppRuntime.AppendLifetimeLog($"thumbbar-setup-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>再生中なら「一時停止」、止まっていれば「再生」のアイコンにする。キューが空なら押せなくする。</summary>
        private void UpdateTaskbarButtons()
        {
            var playback = _runtime.AppState.Playback;
            _thumbButtons?.Update(playback.IsPlaying, playback.HasQueue);
        }

        private void TearDownTaskbarButtons()
        {
            var playback = _runtime.AppState.Playback;
            playback.PlaybackStateChanged -= UpdateTaskbarButtons;
            playback.QueueChanged -= UpdateTaskbarButtons;
            _thumbButtons?.Dispose();
            _thumbButtons = null;
        }

        // ───────── サイドバーのプレイリスト ─────────

        /// <summary>子項目の Tag の接頭辞。後ろにプレイリストのファイルパスが付く。</summary>
        private const string PlaylistTagPrefix = "Playlist:";

        /// <summary>Changed は保存処理の続きなどから呼ばれるので、UI スレッドに乗せ直してから作り直す。</summary>
        private void Playlists_Changed() => DispatcherQueue.TryEnqueue(RefreshPlaylistNav);

        private void RefreshPlaylistNav() => SafeAsync.Run(async () =>
        {
            var list = await _runtime.AppState.Playlists.ListAsync();
            // 自動保存のたびに Changed が来るが、名前・ファイルが同じなら作り直さない（選択が一瞬外れるのを防ぐ）
            if (list.Count == _playlistNavItems.Count
                && list.Select(x => PlaylistTagPrefix + x.Path).SequenceEqual(_playlistNavItems.Select(x => x.Tag as string)))
                return;
            // 子項目は MenuItemsSource（ObservableCollection）経由で入れる。
            // 展開済みの項目の MenuItems を後から直接書き換えても、NavigationView が表示を作り直さないため
            if (NavItemPlaylists.MenuItemsSource == null)
                NavItemPlaylists.MenuItemsSource = _playlistNavItems;
            _playlistNavItems.Clear();
            foreach (var info in list)
            {
                var navItem = new NavigationViewItem
                {
                    Content = info.Name,
                    Tag = PlaylistTagPrefix + info.Path,
                    Icon = new FontIcon { Glyph = "" }
                };
                navItem.RightTapped += PlaylistNavItem_RightTapped;
                _playlistNavItems.Add(navItem);
            }

            // 最初の読み込み後に開く。子項目が無いうちから開いた状態にしておくと、
            // 後から入れた子項目が表示されない（NavigationView の作り直しが起きない）ため
            if (!_playlistNavExpandedOnce && _playlistNavItems.Count > 0)
            {
                _playlistNavExpandedOnce = true;
                NavItemPlaylists.IsExpanded = true;
            }

            // 作り直すと選択が外れるので、表示中のプレイリストを選び直す
            if (ContentFrame.Content is PlaylistDetailPage && _currentPlaylistPath != null)
                UpdateNavSelection(PlaylistTagPrefix + _currentPlaylistPath);
        }, null, "playlist-nav");

        /// <summary>サイドバーの「プレイリスト」の子項目（各プレイリスト）。</summary>
        private readonly System.Collections.ObjectModel.ObservableCollection<NavigationViewItem> _playlistNavItems = new();

        /// <summary>起動後に一度だけ「プレイリスト」を自動で展開したか（以後はユーザーの開閉を尊重する）。</summary>
        private bool _playlistNavExpandedOnce;

        /// <summary>今表示しているプレイリスト詳細のパス（同じプレイリストへの再遷移を省くため）。</summary>
        private string? _currentPlaylistPath;

        // ───────── 戻る／進む（マウスのサイドボタン・Alt+←→） ─────────

        private void SetUpBackForwardInput()
        {
            // マウスの戻る（XButton1）／進む（XButton2）。一覧などが PointerPressed を処理済みにしても受け取れるよう
            // handledEventsToo = true で登録する
            RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), true);

            // キーボード: Alt+← / Alt+→、およびキーボードの「戻る」キー
            AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, GoBack);
            AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, GoForward);
            AddAccelerator(VirtualKey.GoBack, VirtualKeyModifiers.None, GoBack);
            AddAccelerator(VirtualKey.GoForward, VirtualKeyModifiers.None, GoForward);
            // ショートカットのツールチップ表示は不要
            RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
            {
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
                accelerator.Invoked += (_, args) =>
                {
                    action();
                    args.Handled = true;
                };
                RootGrid.KeyboardAccelerators.Add(accelerator);
            }
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (props.IsXButton1Pressed)
            {
                GoBack();
                e.Handled = true;
            }
            else if (props.IsXButton2Pressed)
            {
                GoForward();
                e.Handled = true;
            }
        }

        private void GoBack()
        {
            if (ContentFrame.CanGoBack)
                ContentFrame.GoBack();
        }

        private void GoForward()
        {
            if (ContentFrame.CanGoForward)
                ContentFrame.GoForward();
        }

        /// <summary>
        /// Auto Dark Mode と同じ順序: Navigate → 位置サイズ復元 → Activate。
        /// </summary>
        public void PrepareAndActivate(string? initialPageTag = null)
        {
            string tag = string.IsNullOrEmpty(initialPageTag) ? GetDefaultPageTag() : initialPageTag;
            NavigateToPage(tag, force: true, suppressTransition: true);
            RestoreWindowBounds();
            _windowBoundsReady = true;

            AppWindow.IsShownInSwitchers = true;
            Activate();
        }

        public void NavigateToPageTag(string tag) => NavigateToPage(tag, force: false, suppressTransition: true);

        public void SaveWindowBoundsFromRuntime() => SaveWindowBounds();

        /// <summary>起動直後はライブラリを表示する（再生中の曲がまだ無いため）。</summary>
        private static string GetDefaultPageTag() => "Library";

        private void ApplyWindowIcon()
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(iconPath))
                return;

            try
            {
                AppWindow.SetIcon(iconPath);
            }
            catch
            {
                // SetIcon 失敗時はタイトルバーのフォールバック表示に任せる
            }
        }

        private void ContentFrame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Title = $"{Strings.Get("AppName")} - {e.Exception?.Message}";
        }

        private void ConfigureMinimumWindowSize()
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
                return;

            presenter.IsResizable = true;
            double scaleFactor = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            presenter.PreferredMinimumWidth = (int)(MinimumWindowWidth * scaleFactor);
            presenter.PreferredMinimumHeight = (int)(MinimumWindowHeight * scaleFactor);
            presenter.PreferredMaximumWidth = 10000;
            presenter.PreferredMaximumHeight = 10000;
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (_runtime.IsExitingProcess || !_windowBoundsReady)
                return;

            if (args.DidSizeChange || args.DidPositionChange)
                SaveWindowBounds();
        }

        private void SaveWindowBounds()
        {
            if (!_windowBoundsReady)
                return;

            WindowPlacementHelper.Save(this, _runtime.Settings);
        }

        private void RestoreWindowBounds()
        {
            WindowPlacementHelper.Restore(this, _runtime.Settings, DefaultClientWidth, DefaultClientHeight);
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_runtime.IsExitingProcess)
                return;

            SaveWindowBounds();
            _runtime.AppState.Playlists.Changed -= Playlists_Changed;
            TearDownTaskbarButtons();
            _runtime.OnMainWindowClosing(this);

            // × は本当に Close する（Hide しない）。Closed 後に AppRuntime がプロセスを終了する（LHD 型）
            args.Cancel = false;
            AppWindow.Closing -= AppWindow_Closing;
            AppWindow.Changed -= AppWindow_Changed;
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            // 「プレイリスト」「編集」自体はページを持たない。押したら子項目の開閉だけを切り替える
            if (ReferenceEquals(args.InvokedItemContainer, NavItemPlaylists) || ReferenceEquals(args.InvokedItemContainer, NavItemEdit))
            {
                // NavigationView は ItemInvoked を出した「後」に、子を持つ項目の開閉を自分でも反転する。
                // ここで直接反転すると二重反転で元に戻ってしまう（三角マークでしか開閉できなかった原因）。
                // そこで押した時点の逆を目標として覚え、既定処理が終わった後（次の UI 処理）に確定させる。
                // 既定処理が反転してもしなくても、1 回押す = 1 回開閉 になる
                var group = (NavigationViewItem)args.InvokedItemContainer;
                bool target = !group.IsExpanded;
                DispatcherQueue.TryEnqueue(() => group.IsExpanded = target);
                return;
            }

            if (args.InvokedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
                return;

            // サイドバーの子項目（各プレイリスト）→ そのプレイリストの詳細を開く
            if (tag.StartsWith(PlaylistTagPrefix, StringComparison.Ordinal))
            {
                string path = tag[PlaylistTagPrefix.Length..];
                if (!IsShowingPlaylist(path))
                    ContentFrame.Navigate(typeof(PlaylistDetailPage), path, new SuppressNavigationTransitionInfo());
                return;
            }

            NavigateToPage(tag);
        }

        /// <summary>タイトルバー左端の戻るボタン（WMP と同じ位置）。</summary>
        private void AppTitleBar_BackRequested(TitleBar sender, object args) => GoBack();

        private void PlayerBar_NowPlayingRequested(object? sender, EventArgs e) => NavigateToPage("NowPlaying");

        private void NavigateToPage(string tag, bool force = false, bool suppressTransition = false)
        {
            if (!PageTypes.TryGetValue(tag, out Type? pageType))
                pageType = typeof(LibraryPage);

            if (force || ContentFrame.CurrentSourcePageType != pageType)
            {
                if (suppressTransition)
                    ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
                else
                    ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            }

            UpdateNavSelection(tag);
        }

        /// <summary>ページ内からの遷移（アルバム → 詳細 など）や「戻る」でも Nav の選択を追従させる。</summary>
        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            // 戻れないときはタイトルバーの戻るボタンを灰色（押せない）にする
            AppTitleBar.IsBackButtonEnabled = ContentFrame.CanGoBack;

            // プレイリスト詳細は、サイドバーの該当する子項目を選択状態にする
            _currentPlaylistPath = e.SourcePageType == typeof(PlaylistDetailPage) ? e.Parameter as string : null;
            if (_currentPlaylistPath != null)
            {
                UpdateNavSelection(PlaylistTagPrefix + _currentPlaylistPath);
                return;
            }

            string? tag = PageTypes.FirstOrDefault(p => p.Value == e.SourcePageType).Key;
            if (tag == null)
                DetailPageParents.TryGetValue(e.SourcePageType, out tag);
            if (tag != null)
                UpdateNavSelection(tag);
        }

        private void UpdateNavSelection(string tag)
        {
            // 子項目（プレイリスト・編集の各ページ）も探す
            var editItems = NavItemEdit.MenuItems.OfType<NavigationViewItem>().ToList();
            var match = NavView.MenuItems.OfType<NavigationViewItem>()
                .Concat(_playlistNavItems)
                .Concat(editItems)
                .Concat(NavView.FooterMenuItems.OfType<NavigationViewItem>())
                .FirstOrDefault(item => item.Tag as string == tag);
            // 折りたたまれていても選択中の項目が見えるように、親を開く
            if (match != null && _playlistNavItems.Contains(match))
                NavItemPlaylists.IsExpanded = true;
            if (match != null && editItems.Contains(match))
                NavItemEdit.IsExpanded = true;

            // プレイリストが見つからない（一覧の作り直し前など）ときは何も選ばない。
            // 「プレイリスト」自体は選択できない項目なので、代わりに選ぶこともしない
            if (match == null && tag.StartsWith(PlaylistTagPrefix, StringComparison.Ordinal))
                NavView.SelectedItem = null;
            else
                NavView.SelectedItem = match ?? NavItemLibrary;
        }

        // ───────── サイドバーのプレイリストの右クリックメニュー（旧プレイリスト一覧ページの機能） ─────────

        /// <summary>「プレイリスト」を右クリック: 新しいプレイリスト / フォルダを開く。</summary>
        private void NavItemPlaylists_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            // 子項目の右クリックはそちらのメニューに任せる（イベントは親へも伝わってくるため）
            if (e.Handled)
                return;

            var playlists = _runtime.AppState.Playlists;
            var create = new MenuFlyoutItem { Text = Strings.Get("Menu_NewPlaylist"), Icon = new FontIcon { Glyph = "\uE710" } };
            create.Click += (_, _) => SafeAsync.Run(async () =>
            {
                string? name = await Dialogs.PromptTextAsync(RootGrid.XamlRoot, Strings.Get("Playlist_NewTitle"));
                if (name == null)
                    return;
                string? path = await playlists.CreateAsync(name, PickerHelper.PickFolderAsync);
                if (path != null)
                    ContentFrame.Navigate(typeof(PlaylistDetailPage), path, new SuppressNavigationTransitionInfo());
            }, RootGrid.XamlRoot, "nav-new-playlist");

            var openFolder = new MenuFlyoutItem { Text = Strings.Get("Playlist_OpenFolder/Text"), Icon = new FontIcon { Glyph = "\uE838" } };
            openFolder.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(playlists.Folder);
                    Process.Start("explorer.exe", $"\"{playlists.Folder}\"");
                }
                catch (Exception ex)
                {
                    AppRuntime.AppendLifetimeLog($"open-playlist-folder-failed {ex.Message}");
                }
            };

            var flyout = new MenuFlyout();
            flyout.Items.Add(create);
            flyout.Items.Add(openFolder);
            flyout.ShowAt(NavItemPlaylists, new FlyoutShowOptions { Position = e.GetPosition(NavItemPlaylists) });
            e.Handled = true;
        }

        /// <summary>各プレイリストを右クリック: 再生 / 名前を変更 / 削除。</summary>
        private void PlaylistNavItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not NavigationViewItem item || item.Tag is not string tag || !tag.StartsWith(PlaylistTagPrefix, StringComparison.Ordinal))
                return;

            string path = tag[PlaylistTagPrefix.Length..];
            string name = item.Content as string ?? Path.GetFileNameWithoutExtension(path);
            var state = _runtime.AppState;
            XamlRoot root = RootGrid.XamlRoot;

            var play = new MenuFlyoutItem { Text = Strings.Get("Menu_Play"), Icon = new FontIcon { Glyph = "\uE768" } };
            play.Click += (_, _) => SafeAsync.Run(async () =>
            {
                var doc = await state.Playlists.LoadAsync(path);
                // 見つからない曲は飛ばす（再生が途中で止まらないように）
                state.Playback.PlayTracks(doc.Entries.Where(x => !x.IsMissing).Select(x => x.Track).ToList());
            }, root, "nav-playlist-play");

            var rename = new MenuFlyoutItem { Text = Strings.Get("Menu_Rename"), Icon = new FontIcon { Glyph = "\uE8AC" } };
            rename.Click += (_, _) => SafeAsync.Run(async () =>
            {
                string? newName = await Dialogs.PromptTextAsync(root, Strings.Get("Menu_Rename"), name);
                if (newName == null)
                    return;
                string newPath = state.Playlists.Rename(path, newName);
                // 表示中のプレイリストなら、新しいファイル名で開き直す（古いパスのままだと保存できない）
                if (IsShowingPlaylist(path))
                    ContentFrame.Navigate(typeof(PlaylistDetailPage), newPath, new SuppressNavigationTransitionInfo());
            }, root, "nav-playlist-rename");

            var delete = new MenuFlyoutItem { Text = Strings.Get("Menu_Delete"), Icon = new FontIcon { Glyph = "\uE74D" } };
            delete.Click += (_, _) => SafeAsync.Run(async () =>
            {
                bool ok = await Dialogs.ConfirmAsync(root, Strings.Get("Playlist_DeleteTitle"),
                    Strings.Format("Playlist_DeleteMessage", name), Strings.Get("Menu_Delete"));
                if (!ok)
                    return;
                await state.Playlists.DeleteAsync(path);
                // 表示中のプレイリストを消したら、ライブラリへ移る
                if (IsShowingPlaylist(path))
                    NavigateToPage("Library");
            }, root, "nav-playlist-delete");

            var flyout = new MenuFlyout();
            flyout.Items.Add(play);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(rename);
            flyout.Items.Add(delete);
            flyout.ShowAt(item, new FlyoutShowOptions { Position = e.GetPosition(item) });
            e.Handled = true; // 親（「プレイリスト」）のメニューを出さない
        }

        private bool IsShowingPlaylist(string path) =>
            ContentFrame.Content is PlaylistDetailPage
            && string.Equals(_currentPlaylistPath, path, StringComparison.OrdinalIgnoreCase);
    }
}
