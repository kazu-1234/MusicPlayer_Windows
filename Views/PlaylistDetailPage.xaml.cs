using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace MusicPlayer.Views
{
    /// <summary>
    /// プレイリストの中身の編集（並べ替え・追加・削除）。
    /// 自動保存がオン（既定）なら、変更のあと少し待って m3u に書き込む。
    /// オフなら「保存」で書き込み、未保存のまま別のページへ移ろうとしたら確認する。
    /// どちらの場合も、保存の直前に保存前の内容が自動でバックアップされる（PlaylistService）。
    /// </summary>
    public sealed partial class PlaylistDetailPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;

        // ObservableCollection にすると ListView のドラッグ並べ替えがそのまま反映される
        private readonly ObservableCollection<PlaylistEntry> _entries = new();
        private PlaylistDocument? _doc;
        private bool _dirty;
        private bool _loading;
        private bool _leaveConfirmed;

        /// <summary>並べ替え・削除・追加の「元に戻す（Ctrl+Z）／やり直し（Ctrl+Y）」。</summary>
        private readonly UndoStack<PlaylistEntry> _undo = new();

        /// <summary>ドラッグ開始時点の並び（並べ替えが確定したら履歴に積む）。</summary>
        private System.Collections.Generic.List<PlaylistEntry>? _dragSnapshot;

        /// <summary>
        /// 自動保存の待ち時間。変更のたびに数え直すので、続けて操作している間は保存せず、
        /// 手が止まってから 1 回だけ保存する（ドラッグ 1 回ごとにファイルを書かないため）。
        /// </summary>
        private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(AppConfig.PlaylistAutoSaveDelayMs) };

        /// <summary>
        /// 最後に見出しで並べ替えた列（▲▼ の表示用）。ドラッグや元に戻すで手動の並びになったら null にする。
        /// ファイルには保存しない（曲順そのものが結果なので）。
        /// </summary>
        private SortSpec? _sort;

        private readonly SortHeader _header;

        private bool AutoSave => _state.Settings.PlaylistAutoSave;

        public PlaylistDetailPage()
        {
            InitializeComponent();
            _autoSaveTimer.Tick += (_, _) =>
            {
                _autoSaveTimer.Stop();
                SafeAsync.Run(SaveAsync, XamlRoot, "playlist-autosave");
            };
            // ウィンドウを閉じたときなど、ページが消える前に保存待ちを書き込む
            Unloaded += (_, _) => FlushAutoSave();
            // 見出しを押すと、その順にプレイリストの曲順を書き換える（Ctrl+Z で戻せる・自動保存される）
            _header = new SortHeader(EntriesHeader, () => _sort, ApplySort);
            EntriesList.ItemsSource = _entries;
            _entries.CollectionChanged += Entries_CollectionChanged;
            TrackMenu.Attach(EntriesList, this, () => _entries.Where(x => !x.IsMissing).Cast<object>().ToList(),
                o => o is PlaylistEntry { IsMissing: false } entry ? entry.Track : null);

            A11y.Label(MoveUpButton, Strings.Get("Playlist_MoveUp"));
            A11y.Label(MoveDownButton, Strings.Get("Playlist_MoveDown"));
            UpdateSelectionButtons();
            UndoKeys.Attach(this, UndoEdit, RedoEdit);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e) => SafeAsync.Run(() => OnNavigatedToAsync(e), XamlRoot, "OnNavigatedTo");

        private async Task OnNavigatedToAsync(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is not string path)
                return;

            TitleText.Text = Path.GetFileNameWithoutExtension(path);
            // 自動保存のときは「保存」ボタンと区切りを隠す（保存は自動で行う）
            SaveButton.Visibility = SaveSeparator.Visibility = AutoSave ? Visibility.Collapsed : Visibility.Visible;
            // ライブラリに入っている曲だけが再生できる（グレーにならない）ので、ライブラリが変わったら見直す
            _state.Library.Changed -= Library_Changed;
            _state.Library.Changed += Library_Changed;
            await LoadEntriesAsync(path);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // 表示していない間はライブラリの変化を受けない
            _state.Library.Changed -= Library_Changed;
        }

        /// <summary>m3u を読み込んで一覧に並べる（開いたとき・ライブラリが変わったとき）。</summary>
        private async Task LoadEntriesAsync(string path)
        {
            LoadingRing.IsActive = true;
            try
            {
                _doc = await _state.Playlists.LoadAsync(path);
                _loading = true;
                _entries.Clear();
                foreach (var entry in _doc.Entries)
                    _entries.Add(entry);
                _loading = false;
                _undo.Clear(); // 読み直したら履歴は持ち越さない（古い行に戻すと再生可否が古いままになるため）
                ClearSortMark();
                SetDirty(false);
            }
            catch (Exception ex)
            {
                await Dialogs.ShowErrorAsync(XamlRoot, ex.Message);
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        /// <summary>
        /// ライブラリの読み込み・再スキャンが終わったとき。起動直後などでライブラリがまだ空のうちに開くと
        /// 全曲がグレーになるので、再生できるかどうかが変わる曲があれば読み直す。
        /// 未保存の変更（保存待ちを含む）があるときは、編集内容を消さないよう読み直さない。
        /// </summary>
        private void Library_Changed() => DispatcherQueue.TryEnqueue(() =>
        {
            if (_doc == null || _loading || _dirty || _autoSaveTimer.IsEnabled)
                return;
            // 読み込み時（PlaylistService.LoadAsync）と同じ条件で、再生できるかどうかが変わった曲があるか調べる
            bool changed = _entries.Any(x => x.IsMissing == (_state.Library.FindByPath(x.Track.Path) != null && File.Exists(x.Track.Path)));
            if (changed)
                SafeAsync.Run(() => LoadEntriesAsync(_doc.Path), XamlRoot, "playlist-reload");
        });

        /// <summary>未保存の変更があれば、保存する／破棄する／留まる を選んでもらう。</summary>
        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e) => SafeAsync.Run(() => OnNavigatingFromAsync(e), XamlRoot, "OnNavigatingFrom");

        private async Task OnNavigatingFromAsync(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            // 自動保存なら確認せず、保存待ちをすぐ書き込んで移動する
            if (AutoSave)
            {
                FlushAutoSave();
                return;
            }
            if (!_dirty || _leaveConfirmed)
                return;

            // いったん遷移を止め、ダイアログの結果を見てから改めて遷移し直す
            e.Cancel = true;
            var dialog = new ContentDialog
            {
                Title = Strings.Get("Playlist_UnsavedTitle"),
                Content = Strings.Format("Playlist_UnsavedMessage", TitleText.Text),
                PrimaryButtonText = Strings.Get("Common_Save"),
                SecondaryButtonText = Strings.Get("Common_DontSave"),
                CloseButtonText = Strings.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
                return;
            if (result == ContentDialogResult.Primary && !await SaveAsync())
                return;

            _leaveConfirmed = true;
            // 止めた遷移を同じ種類でやり直す（戻る／進む＝マウスのサイドボタンや Alt+←→ にも対応）
            if (e.NavigationMode == NavigationMode.Back)
                Frame.GoBack();
            else if (e.NavigationMode == NavigationMode.Forward)
                Frame.GoForward();
            else
                Frame.Navigate(e.SourcePageType, e.Parameter);
        }

        private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_loading)
                SetDirty(true);
            CountText.Text = Strings.Format("Playlist_CountFormat", _entries.Count);
        }

        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            SaveButton.IsEnabled = dirty;
            // 自動保存のときは「未保存」を出さない（すぐ保存されるため）
            DirtyText.Visibility = dirty && !AutoSave ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = Strings.Format("Playlist_CountFormat", _entries.Count);

            if (dirty && AutoSave)
            {
                // Stop → Start で待ち時間を最初から数え直す
                _autoSaveTimer.Stop();
                _autoSaveTimer.Start();
            }
        }

        /// <summary>保存待ちがあれば今すぐ保存する（ページを離れるとき・閉じるとき）。</summary>
        private void FlushAutoSave()
        {
            if (!_autoSaveTimer.IsEnabled)
                return;
            _autoSaveTimer.Stop();
            // SaveAsync は最初の await より前に今の曲順を写し取るので、ページが消えても正しい内容で保存される
            SafeAsync.Run(SaveAsync, null, "playlist-autosave-flush");
        }

        /// <summary>並べ替えメニューで選んだ順に、プレイリストの曲順を書き換える（Ctrl+Z で戻せる）。</summary>
        private void ApplySort(SortSpec spec)
        {
            _sort = spec;
            var sorted = _entries.OrderBy(x => x, System.Collections.Generic.Comparer<PlaylistEntry>.Create(
                (a, b) => SortService.CompareTracks(a.Track, b.Track, spec))).ToList();
            if (sorted.SequenceEqual(_entries))
            {
                ListScroll.ToTop(EntriesList); // 並びは同じでも、見出しを押したら一番上から見せる
                return; // 並びが変わらなければ履歴にも積まない
            }
            _undo.Push(_entries);
            ListDiff.ApplyOrder(_entries, sorted, x => (object)x);
            // 並べ替えたら一番上から見せる（途中の位置のままだと、並びが変わって少し下にずれたように見えるため）
            ListScroll.ToTop(EntriesList);
        }

        private async Task<bool> SaveAsync()
        {
            if (_doc == null)
                return false;

            try
            {
                _doc.Entries = _entries.ToList();
                await _state.Playlists.SaveAsync(_doc);
                SetDirty(false);
                return true;
            }
            catch (Exception ex)
            {
                await Dialogs.ShowErrorAsync(XamlRoot, ex.Message);
                return false;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => SaveButton_ClickAsync(sender, e), XamlRoot, "SaveButton_Click");

        private async Task SaveButton_ClickAsync(object sender, RoutedEventArgs e) => await SaveAsync();

        private void PlayButton_Click(object sender, RoutedEventArgs e) => Play(shuffle: false);

        private void ShuffleButton_Click(object sender, RoutedEventArgs e) => Play(shuffle: true);

        /// <summary>見つからない曲を除いて再生する（再生が途中で止まらないように）。</summary>
        private void Play(bool shuffle, PlaylistEntry? startFrom = null)
        {
            var playable = _entries.Where(x => !x.IsMissing).ToList();
            if (playable.Count == 0)
                return;

            int start = startFrom == null ? 0 : Math.Max(0, playable.IndexOf(startFrom));
            _state.Playback.PlayTracks(playable.Select(x => x.Track).ToList(), start, shuffle);
        }

        private void EntriesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistEntry { IsMissing: false } entry)
                Play(shuffle: false, entry);
        }

        private void AddFilesButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => AddFilesButton_ClickAsync(sender, e), XamlRoot, "AddFilesButton_Click");

        private async Task AddFilesButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            var files = await PickerHelper.PickFilesAsync(AppConfig.AudioExtensions);
            await AddPathsAsync(files);
        }

        private async Task AddPathsAsync(System.Collections.Generic.IEnumerable<string> paths)
        {
            var targets = paths.Where(LibraryService.IsAudioFile).ToList();
            // ライブラリ外のファイルはタグ読み取りが入るので、UI を止めないよう別スレッドで解決する
            var tracks = await Task.Run(() => targets.Select(p => _state.Library.GetOrRead(p)).ToList());
            if (tracks.Count > 0)
                _undo.Push(_entries);
            foreach (var t in tracks)
                _entries.Add(PlaylistService.NewEntry(t));
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        private void MoveDownButton_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

        /// <summary>選択中の行をまとめて 1 行ずつ上下に動かす（端にぶつかったら動かさない）。</summary>
        private void MoveSelected(int delta)
        {
            var selected = EntriesList.SelectedItems.OfType<PlaylistEntry>()
                .Select(x => _entries.IndexOf(x)).OrderBy(i => i).ToList();
            if (selected.Count == 0)
                return;
            if (delta < 0 && selected[0] == 0) return;
            if (delta > 0 && selected[^1] == _entries.Count - 1) return;

            _undo.Push(_entries);
            ClearSortMark(); // 上下ボタンで手で動かしたので、見出しの ▲▼ は外す
            var items = selected.Select(i => _entries[i]).ToList();
            // 下へ動かすときは下の行から動かさないと、隣同士で入れ替わってしまう
            foreach (int i in delta < 0 ? selected : Enumerable.Reverse(selected))
                _entries.Move(i, i + delta);

            foreach (var item in items)
                EntriesList.SelectedItems.Add(item);
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (EntriesList.SelectedItems.Count > 0)
                _undo.Push(_entries);
            foreach (var entry in EntriesList.SelectedItems.OfType<PlaylistEntry>().ToList())
                _entries.Remove(entry);
        }

        // ───────── ドラッグ並べ替え・元に戻す ─────────

        private void EntriesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => _dragSnapshot = _entries.ToList();

        /// <summary>ドラッグで動かし終えたら、動かす前の並びを履歴に積む（動いていなければ何もしない）。</summary>
        private void EntriesList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            var before = _dragSnapshot;
            _dragSnapshot = null;
            if (before != null && !before.SequenceEqual(_entries))
            {
                _undo.Push(before);
                ClearSortMark(); // 手で並べ替えたので、見出しの ▲▼ は外す
            }
        }

        private void UndoEdit()
        {
            Restore(_undo.Undo(_entries));
            ClearSortMark();
        }

        private void RedoEdit()
        {
            Restore(_undo.Redo(_entries));
            ClearSortMark();
        }

        private void ClearSortMark()
        {
            _sort = null;
            _header.Refresh();
        }

        /// <summary>履歴の並びで一覧を置き換える（CollectionChanged で未保存扱いになる）。</summary>
        private void Restore(System.Collections.Generic.List<PlaylistEntry>? state)
        {
            if (state == null)
                return;
            // Clear して入れ直すと全行を作り直して重いので、違う位置だけ Move / Insert / Remove する。
            // 同じ曲が 2 回入っていても別の行として扱えるよう、キーは項目そのもの（参照）にする
            ListDiff.ApplyOrder(_entries, state, x => (object)x);
        }

        private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionButtons();

        private void UpdateSelectionButtons()
        {
            bool any = EntriesList.SelectedItems.Count > 0;
            MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = RemoveButton.IsEnabled = any;
        }

        /// <summary>エクスプローラーからのファイルドロップを受け付ける（一覧内の並べ替えドラッグはそのまま通す）。</summary>
        private void EntriesList_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                e.AcceptedOperation = DataPackageOperation.Copy;
        }

        private void EntriesList_Drop(object sender, DragEventArgs e) => SafeAsync.Run(() => EntriesList_DropAsync(sender, e), XamlRoot, "EntriesList_Drop");

        private async Task EntriesList_DropAsync(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            var items = await e.DataView.GetStorageItemsAsync();
            await AddPathsAsync(items.OfType<StorageFile>().Select(f => f.Path));
        }
    }
}
