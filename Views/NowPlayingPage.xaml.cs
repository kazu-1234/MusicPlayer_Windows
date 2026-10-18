using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Views
{
    /// <summary>
    /// 再生キューの 1 行。同じ曲がキューに 2 回入っていても別の行として扱えるよう、Track を直接並べずに包む
    /// （ListView のドラッグ並べ替えは「同じオブジェクトが 2 つある」と正しく動かないため）。
    /// </summary>
    public sealed class QueueEntry
    {
        public QueueEntry(QueueSlot slot) => Slot = slot;
        public QueueSlot Slot { get; }
        public Track Track => Slot.Track;
    }

    /// <summary>再生中の曲（大きなジャケット）と再生キュー。キューはドラッグで並べ替えでき、Ctrl+Z / Ctrl+Y で戻せる。</summary>
    public sealed partial class NowPlayingPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;

        /// <summary>プログラムから選択行を動かしている最中（クリック扱いしないため）。</summary>
        private bool _syncingSelection;

        /// <summary>ドラッグ並べ替えがそのまま反映されるよう ObservableCollection で表示する。</summary>
        private readonly ObservableCollection<QueueEntry> _queue = new();

        // 元に戻す履歴はページを離れても残す（このページは表示のたびに作り直されるので static にする）
        private static readonly UndoStack<QueueSlot> Undo = new();
        private static int _undoGeneration = -1;

        /// <summary>ドラッグ開始時点のキュー（並べ替えが確定したら元に戻す履歴に積む）。</summary>
        private System.Collections.Generic.List<QueueSlot>? _dragSnapshot;

        public NowPlayingPage()
        {
            InitializeComponent();
            ArtHost.Width = ArtHost.Height = AppConfig.NowPlayingArtSize;
            QueueList.ItemsSource = _queue;
            ArtistAlbum.LineMaxWidth = AppConfig.NowPlayingArtSize;
            ArtistAlbum.ArtistClick += track => LibraryNavigation.GoToArtist(Frame, track);
            ArtistAlbum.AlbumClick += track => LibraryNavigation.GoToAlbum(Frame, track);
            UndoKeys.Attach(this, UndoQueue, RedoQueue);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _state.Playback.CurrentTrackChanged += UpdateCurrent;
            _state.Playback.QueueChanged += UpdateQueue;
            UpdateQueue();
            UpdateCurrent();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _state.Playback.CurrentTrackChanged -= UpdateCurrent;
            _state.Playback.QueueChanged -= UpdateQueue;
        }

        private void UpdateQueue()
        {
            // 別の曲一覧で再生し直した（キューが丸ごと替わった）ら、前のキューの履歴は捨てる
            if (_undoGeneration != _state.Playback.QueueGeneration)
            {
                Undo.Clear();
                _undoGeneration = _state.Playback.QueueGeneration;
            }

            // 全件作り直すと ListView が全行（とジャケット）を作り直して重いので、違うところだけ直す。
            // ドラッグ直後は表示がすでに新しい順序になっているので、ここでは何も起きない
            var slots = _state.Playback.QueueSlots;
            if (!ListDiff.SameOrder(_queue, slots, x => x.Slot.Id, x => x.Id))
                ListDiff.ApplyOrder(_queue, slots.Select(s => new QueueEntry(s)).ToList(), x => x.Slot.Id);
            EmptyText.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncSelection();
        }

        // ───────── 並べ替え・元に戻す ─────────

        private System.Collections.Generic.List<QueueSlot> CurrentSlots() => _queue.Select(x => x.Slot).ToList();

        private void QueueList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => _dragSnapshot = CurrentSlots();

        /// <summary>ドラッグで行を動かし終えたら、その順序を再生キューへ反映する。</summary>
        private void QueueList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            var before = _dragSnapshot;
            _dragSnapshot = null;
            var after = CurrentSlots();
            if (before == null || before.SequenceEqual(after))
                return; // 動かさずに離した

            Undo.Push(before);
            _state.Playback.ReorderQueue(after);
        }

        private void UndoQueue()
        {
            var state = Undo.Undo(CurrentSlots());
            if (state != null)
                _state.Playback.ReorderQueue(state);
        }

        private void RedoQueue()
        {
            var state = Undo.Redo(CurrentSlots());
            if (state != null)
                _state.Playback.ReorderQueue(state);
        }

        private void UpdateCurrent() => SafeAsync.Run(() => UpdateCurrentAsync(), null, "UpdateCurrent");

        private async Task UpdateCurrentAsync()
        {
            Track? track = _state.Playback.CurrentTrack;
            TitleText.Text = track?.DisplayTitle ?? "";
            // 再生バー（PlayerBar）と同じ「アーティスト • アルバム」の形式にそろえる（それぞれ押せる）
            ArtistAlbum.Track = track;
            ArtImage.Source = null;
            AudioInfoList.ItemsSource = null;
            SyncSelection();

            if (track == null)
                return;

            // 詳細はファイルを開いて読むので別スレッドで。ジャケットの読み込みと並行して進める
            var infoTask = Task.Run(() => AudioInfoService.Read(track));

            string? art = await ArtworkCache.GetOrCreateAsync(ArtworkCache.KeyFor(track.Path), track.Path);
            if (art != null && ReferenceEquals(track, _state.Playback.CurrentTrack))
                ArtImage.Source = new BitmapImage(new Uri(art)) { DecodePixelWidth = (int)AppConfig.ArtworkCacheSize };

            var info = await infoTask;
            // 待っている間に次の曲へ進んでいたら古い曲の詳細は出さない
            if (!ReferenceEquals(track, _state.Playback.CurrentTrack))
                return;
            AudioInfoList.ItemsSource = AudioInfoService.FormatLines(info);
        }

        /// <summary>キューの中の再生中の行を選択状態にして、見える位置までスクロールする。</summary>
        private void SyncSelection()
        {
            int index = _state.Playback.CurrentPlayOrderIndex;
            _syncingSelection = true;
            QueueList.SelectedIndex = index;
            _syncingSelection = false;
            if (index >= 0 && _queue.Count > index)
                QueueList.ScrollIntoView(_queue[index]);
        }

        private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_syncingSelection)
                return;

            if (e.ClickedItem is QueueEntry entry)
                _state.Playback.JumpToPlayOrderIndex(_queue.IndexOf(entry));
        }

        /// <summary>キューの行のアーティスト名を押したら、そのアーティストのページへ。</summary>
        private void ArtistLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is QueueEntry entry)
                LibraryNavigation.GoToArtist(Frame, entry.Track);
        }

        /// <summary>右クリックで「キューから削除」。</summary>
        private void QueueList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not QueueEntry entry)
                return;

            int index = _queue.IndexOf(entry);
            var remove = new MenuFlyoutItem { Text = Strings.Get("Menu_RemoveFromQueue"), Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) =>
            {
                // 削除も Ctrl+Z で戻せるよう、消す前のキューを記録する
                Undo.Push(CurrentSlots());
                _state.Playback.RemoveFromQueue(index);
            };
            var flyout = new MenuFlyout();
            flyout.Items.Add(remove);
            flyout.ShowAt(QueueList, new FlyoutShowOptions { Position = e.GetPosition(QueueList) });
            e.Handled = true;
        }
    }
}
