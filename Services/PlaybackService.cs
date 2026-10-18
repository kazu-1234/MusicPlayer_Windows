using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using MusicPlayer.Models;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MusicPlayer.Services
{
    public enum RepeatMode
    {
        Off,
        All,
        One
    }

    /// <summary>
    /// 再生キューの 1 曲分。Id は再生アイテムごとの通し番号で、同じ曲が 2 回入っていても区別できる
    /// （並べ替え・元に戻すで「どのアイテムか」を特定するのに使う）。
    /// </summary>
    public readonly record struct QueueSlot(int Id, Track Track);

    /// <summary>
    /// 再生エンジン。Windows 標準の MediaPlayer + MediaPlaybackList を使う。
    /// - FLAC / MP3 / WAV / M4A を追加 DLL なしで再生できる
    /// - MediaPlaybackList は次の曲を先読みするので曲間が途切れない（ギャップレス）
    /// - メディアキーやタスクバーの再生コントロール（SMTC）と自動で連携する
    /// AppRuntime が所有するため、ウィンドウを作り直しても再生は続く。
    /// </summary>
    public sealed class PlaybackService : IDisposable
    {
        /// <summary>MediaSource.CustomProperties に入れる、曲を引き当てるための ID のキー名。</summary>
        private const string IdKey = "mp.id";

        private readonly DispatcherQueue _ui;
        private readonly Settings _settings;
        private readonly MediaPlayer _player = new();
        private MediaPlaybackList _list = new();

        // MediaPlaybackItem → Track の対応表（ID は MediaSource.CustomProperties に保存する）
        private readonly Dictionary<int, Track> _tracksById = new();
        private int _nextId;

        public PlaybackService(DispatcherQueue ui, Settings settings)
        {
            _ui = ui;
            _settings = settings;

            _player.AudioCategory = MediaPlayerAudioCategory.Media;
            _player.Volume = Math.Clamp(settings.Volume, 0, 100) / 100.0;
            _player.PlaybackSession.PlaybackStateChanged += (_, _) => OnUi(() => PlaybackStateChanged?.Invoke());
            _player.MediaFailed += (_, e) =>
            {
                // 再生できなかった理由は後から調べられるよう lifetime.log に残す（頻繁には起きない）
                AppRuntime.AppendLifetimeLog($"media-failed {e.Error} {e.ExtendedErrorCode?.HResult:X8} {e.ErrorMessage}");
                OnUi(() => PlaybackFailed?.Invoke(e.ErrorMessage));
            };
            AttachList(_list);
        }

        // ───────── 状態 ─────────

        public Track? CurrentTrack { get; private set; }

        public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public TimeSpan Position => _player.PlaybackSession.Position;

        public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;

        public bool HasQueue => _list.Items.Count > 0;

        public bool Shuffle
        {
            get => _settings.Shuffle;
            set
            {
                _settings.Shuffle = value;
                _settings.Save();
                _list.ShuffleEnabled = value;
                ModesChanged?.Invoke();
                QueueChanged?.Invoke();
            }
        }

        public RepeatMode Repeat
        {
            get => _settings.Repeat;
            set
            {
                _settings.Repeat = value;
                _settings.Save();
                // 全曲リピートは MediaPlaybackList に任せる。1 曲リピートは CurrentItemChanged で戻す。
                _list.AutoRepeatEnabled = value == RepeatMode.All;
                ModesChanged?.Invoke();
            }
        }

        /// <summary>音量（0〜100）。</summary>
        public double Volume
        {
            get => _player.Volume * 100;
            set
            {
                _player.Volume = Math.Clamp(value, 0, 100) / 100.0;
                // スライダーのドラッグ中は何十回も呼ばれるので、ここでは保存しない（終了時に保存する）
                _settings.Volume = value;
            }
        }

        /// <summary>再生順（シャッフル中はシャッフル後の順）の曲一覧。再生中画面のキュー表示に使う。</summary>
        public IReadOnlyList<Track> PlayOrder =>
            OrderedItems().Select(TrackOf).Where(t => t != null).Select(t => t!).ToList();

        /// <summary>再生順のキュー（ID 付き）。並べ替え・元に戻す用。</summary>
        public IReadOnlyList<QueueSlot> QueueSlots =>
            OrderedItems()
                .Select(item => (Id: IdOf(item), Track: TrackOf(item)))
                .Where(x => x.Track != null)
                .Select(x => new QueueSlot(x.Id, x.Track!))
                .ToList();

        /// <summary>
        /// キューを丸ごと差し替えた回数（PlayTracks のたびに増える）。
        /// 画面側はこれが変わったら「元に戻す」の履歴を捨てる（別のキューの履歴で戻さないため）。
        /// </summary>
        public int QueueGeneration { get; private set; }

        /// <summary>PlayOrder の中で現在の曲が何番目か（無ければ -1）。</summary>
        public int CurrentPlayOrderIndex
        {
            get
            {
                var current = _list.CurrentItem;
                if (current == null)
                    return -1;

                int i = 0;
                foreach (var item in OrderedItems())
                {
                    if (IdOf(item) == IdOf(current))
                        return i;
                    i++;
                }
                return -1;
            }
        }

        // ───────── イベント（すべて UI スレッドで発火） ─────────

        public event Action? CurrentTrackChanged;
        public event Action? PlaybackStateChanged;
        public event Action? QueueChanged;
        public event Action? ModesChanged;
        public event Action<string>? PlaybackFailed;

        // ───────── 操作 ─────────

        /// <summary>曲一覧で再生キューを置き換えて、startIndex の曲から再生する。</summary>
        public void PlayTracks(IReadOnlyList<Track> tracks, int startIndex = 0, bool? shuffle = null)
        {
            if (tracks.Count == 0)
                return;

            if (shuffle != null && shuffle.Value != _settings.Shuffle)
            {
                _settings.Shuffle = shuffle.Value;
                _settings.Save();
            }

            // 新しいリストを作って差し替える（古いリストのイベントは外す）
            QueueGeneration++;
            DetachList(_list);
            _tracksById.Clear();
            _list = new MediaPlaybackList
            {
                ShuffleEnabled = _settings.Shuffle,
                AutoRepeatEnabled = _settings.Repeat == RepeatMode.All,
                // 次の曲を数秒前から準備して、曲間の無音を無くす
                MaxPrefetchTime = TimeSpan.FromSeconds(5)
            };
            AttachList(_list);

            foreach (var track in tracks)
                _list.Items.Add(CreateItem(track));

            startIndex = Math.Clamp(startIndex, 0, tracks.Count - 1);
            // シャッフル時に「全曲をシャッフル再生」なら先頭曲もランダムにする
            if (shuffle == true)
                startIndex = Random.Shared.Next(tracks.Count);
            _list.StartingItem = _list.Items[startIndex];

            _player.Source = _list;
            _player.Play();

            ModesChanged?.Invoke();
            QueueChanged?.Invoke();
        }

        /// <summary>キューの最後に追加する（何も再生していなければそのまま再生を始める）。</summary>
        public void Enqueue(IEnumerable<Track> tracks)
        {
            var list = tracks.ToList();
            if (!HasQueue)
            {
                PlayTracks(list);
                return;
            }

            foreach (var track in list)
                _list.Items.Add(CreateItem(track));
            QueueChanged?.Invoke();
        }

        /// <summary>現在の曲の次に挟み込む。</summary>
        public void PlayNext(IEnumerable<Track> tracks)
        {
            var list = tracks.ToList();
            if (!HasQueue)
            {
                PlayTracks(list);
                return;
            }

            int insertAt = (int)_list.CurrentItemIndex + 1;
            var newItems = new List<MediaPlaybackItem>();
            foreach (var track in list)
            {
                var item = CreateItem(track);
                _list.Items.Insert(Math.Min(insertAt++, _list.Items.Count), item);
                newItems.Add(item);
            }

            // シャッフル中は追加した曲がランダムな位置に入るので、現在の曲の直後へ並べ直す
            if (_list.ShuffleEnabled && _list.CurrentItem != null)
            {
                var order = _list.ShuffledItems.Where(i => !newItems.Any(n => IdOf(n) == IdOf(i))).ToList();
                int pos = order.FindIndex(i => IdOf(i) == IdOf(_list.CurrentItem));
                order.InsertRange(pos + 1, newItems);
                _list.SetShuffledItems(order);
            }

            QueueChanged?.Invoke();
        }

        public void PlayPause()
        {
            if (!HasQueue)
                return;

            if (IsPlaying)
                _player.Pause();
            else
                _player.Play();
        }

        public void Next()
        {
            if (HasQueue)
                _list.MoveNext();
        }

        /// <summary>数秒以上進んでいたら曲の頭へ、そうでなければ前の曲へ（一般的なプレーヤーと同じ挙動）。</summary>
        public void Previous()
        {
            if (!HasQueue)
                return;

            if (Position.TotalSeconds > AppConfig.PreviousRestartThresholdSeconds || _list.MovePrevious() == null)
                Seek(TimeSpan.Zero);
        }

        public void Seek(TimeSpan position)
        {
            if (_player.PlaybackSession.CanSeek)
                _player.PlaybackSession.Position = position;
        }

        /// <summary>再生順の index 番目の曲へ飛ぶ（再生中画面のキューをクリックしたとき）。</summary>
        public void JumpToPlayOrderIndex(int index)
        {
            var ordered = OrderedItems().ToList();
            if (index < 0 || index >= ordered.Count)
                return;

            int id = IdOf(ordered[index]);
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (IdOf(_list.Items[i]) == id)
                {
                    _list.MoveTo((uint)i);
                    _player.Play();
                    return;
                }
            }
        }

        /// <summary>再生順の index 番目の曲をキューから外す（再生中の曲は外さない）。</summary>
        public void RemoveFromQueue(int playOrderIndex)
        {
            var ordered = OrderedItems().ToList();
            if (playOrderIndex < 0 || playOrderIndex >= ordered.Count)
                return;

            int id = IdOf(ordered[playOrderIndex]);
            if (_list.CurrentItem != null && IdOf(_list.CurrentItem) == id)
                return;

            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (IdOf(_list.Items[i]) == id)
                {
                    _list.Items.RemoveAt(i);
                    _tracksById.Remove(id);
                    break;
                }
            }
            QueueChanged?.Invoke();
        }

        /// <summary>
        /// キューを指定した再生順に並べ直す（ドラッグ並べ替え・元に戻す／やり直しから呼ぶ）。
        /// newOrder に無いアイテムはキューから外し、ID が見つからない曲（削除を元に戻した曲）は作り直して入れる。
        /// <para>
        /// 軽くするため、位置が変わったアイテムだけを動かす（1 曲のドラッグなら移動 1 回）。
        /// 以前は全アイテムを外して入れ直していたため、数百曲のキューで WinRT 呼び出しが何百回も走り重かった。
        /// 再生中の曲のアイテムは一度も外さないので、並べ替えても再生は途切れない。
        /// </para>
        /// </summary>
        public void ReorderQueue(IReadOnlyList<QueueSlot> newOrder)
        {
            if (!HasQueue)
                return;

            var current = _list.CurrentItem;
            var byId = new Dictionary<int, MediaPlaybackItem>();
            foreach (var item in _list.Items)
                byId[IdOf(item)] = item;

            // 目標の並び（MediaPlaybackItem の列）を作る。キューに無い曲は新しくアイテムを作る
            var ordered = new List<MediaPlaybackItem>();
            var used = new HashSet<int>();
            bool added = false;
            foreach (var slot in newOrder)
            {
                if (byId.TryGetValue(slot.Id, out var item))
                {
                    // 同じ ID が 2 回渡されても 1 回だけ使う（同じアイテムを 2 か所に置くことはできない）
                    if (used.Add(slot.Id))
                        ordered.Add(item);
                }
                else
                {
                    ordered.Add(CreateItem(slot.Track));
                    added = true;
                }
            }
            // 再生中の曲は消さない（並べ替え後の一覧に無くても先頭に残す）
            if (current != null && !ordered.Contains(current))
                ordered.Insert(0, current);

            // 1) 目標に無いアイテムを外す（後ろから外すと添字がずれない）
            var keep = new HashSet<MediaPlaybackItem>(ordered);
            bool removed = false;
            for (int i = _list.Items.Count - 1; i >= 0; i--)
            {
                var item = _list.Items[i];
                if (keep.Contains(item))
                    continue;
                _list.Items.RemoveAt(i);
                _tracksById.Remove(IdOf(item));
                removed = true;
            }

            // 2) 新しく作ったアイテムを末尾に足す（位置は次の手順で合わせる）
            if (added)
            {
                var present = new HashSet<MediaPlaybackItem>(_list.Items);
                foreach (var item in ordered)
                {
                    if (!present.Contains(item))
                        _list.Items.Add(item);
                }
            }

            bool changed = removed || added;
            if (_list.ShuffleEnabled)
            {
                // シャッフル中は Items の並びではなく ShuffledItems が再生順。Items には触らず、順序を 1 回渡すだけで済む
                if (changed || !ordered.SequenceEqual(_list.ShuffledItems))
                {
                    _list.SetShuffledItems(ordered);
                    changed = true;
                }
            }
            else
            {
                changed |= MoveIntoOrder(ordered, current);
            }

            // 何も変わっていなければ通知しない（画面の作り直しを起こさない）
            if (changed)
                QueueChanged?.Invoke();
        }

        /// <summary>
        /// _list.Items を ordered と同じ並びにする。先頭から比べて、違う位置だけ Remove + Insert で動かす。
        /// 再生中の曲を外すと再生が止まるので、そこに来るべきなのが再生中の曲なら、代わりに今そこにある別のアイテムを後ろへ送る。
        /// </summary>
        /// <returns>1 つでも動かしたら true。</returns>
        private bool MoveIntoOrder(List<MediaPlaybackItem> ordered, MediaPlaybackItem? current)
        {
            bool moved = false;
            // 1 回のループで 1 つずつ位置が確定する。念のため回数に上限を付け、万一でも無限ループにしない
            int guard = ordered.Count * 2 + 4;
            int i = 0;
            while (i < ordered.Count && i < _list.Items.Count && guard-- > 0)
            {
                var want = ordered[i];
                var have = _list.Items[i];
                if (ReferenceEquals(want, have))
                {
                    i++;
                    continue;
                }

                if (!ReferenceEquals(want, current))
                {
                    // 欲しいアイテムを今の位置から i へ動かす
                    int from = IndexOfItem(want, i + 1);
                    if (from < 0)
                    {
                        i++;
                        continue;
                    }
                    _list.Items.RemoveAt(from);
                    _list.Items.Insert(i, want);
                    i++;
                }
                else
                {
                    // ここに来るべきなのは再生中の曲 → 再生中の曲は動かさず、今 i にある別のアイテムを本来の位置へ送る
                    int target = ordered.IndexOf(have);
                    _list.Items.RemoveAt(i);
                    _list.Items.Insert(Math.Min(Math.Max(target, i), _list.Items.Count), have);
                }
                moved = true;
            }
            return moved;
        }

        private int IndexOfItem(MediaPlaybackItem item, int start)
        {
            for (int j = start; j < _list.Items.Count; j++)
            {
                if (ReferenceEquals(_list.Items[j], item))
                    return j;
            }
            return -1;
        }

        public void Dispose()
        {
            DetachList(_list);
            _player.Dispose();
        }

        // ───────── 内部処理 ─────────

        /// <summary>
        /// 曲から再生アイテムを作る。MediaSource.CreateFromUri はファイルを「実際に再生（先読み）する直前」まで開かないので、
        /// 何千曲をキューに入れても一瞬で終わる。
        /// ※ MediaBinder + StorageFile 方式はパッケージ化しないアプリだと SourceNotSupported になるため使わない。
        /// </summary>
        private MediaPlaybackItem CreateItem(Track track)
        {
            int id = ++_nextId;
            _tracksById[id] = track;

            // new Uri(パス) は空白・# なども正しくエスケープした file:/// URI にしてくれる
            var source = MediaSource.CreateFromUri(new Uri(track.Path));
            source.CustomProperties[IdKey] = id;

            var item = new MediaPlaybackItem(source);
            ApplyBasicDisplayProperties(item, track);
            return item;
        }

        /// <summary>SMTC（メディアキーのポップアップ等）に出す曲名・アーティスト。</summary>
        private static void ApplyBasicDisplayProperties(MediaPlaybackItem item, Track track)
        {
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = track.DisplayTitle;
            props.MusicProperties.Artist = track.DisplayArtist;
            props.MusicProperties.AlbumTitle = track.DisplayAlbum;
            item.ApplyDisplayProperties(props);
        }

        /// <summary>現在の曲になったときだけジャケットを SMTC に設定する（全曲分を先に読まない）。</summary>
        private static async Task ApplyThumbnailAsync(MediaPlaybackItem item, Track track)
        {
            try
            {
                string? art = await ArtworkCache.GetOrCreateAsync(ArtworkCache.KeyFor(track.Path), track.Path);
                if (art == null)
                    return;

                var file = await StorageFile.GetFileFromPathAsync(art);
                var props = item.GetDisplayProperties();
                props.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                item.ApplyDisplayProperties(props);
            }
            catch
            {
            }
        }

        private void AttachList(MediaPlaybackList list)
        {
            list.CurrentItemChanged += List_CurrentItemChanged;
            list.ItemFailed += List_ItemFailed;
        }

        private void DetachList(MediaPlaybackList list)
        {
            list.CurrentItemChanged -= List_CurrentItemChanged;
            list.ItemFailed -= List_ItemFailed;
        }

        /// <summary>
        /// リスト再生中の失敗は MediaPlayer.MediaFailed ではなくこちらに来る（壊れたファイル・非対応形式など）。
        /// 原因を後から調べられるよう lifetime.log に残す。
        /// </summary>
        private void List_ItemFailed(MediaPlaybackList sender, MediaPlaybackItemFailedEventArgs args)
        {
            var track = TrackOf(args.Item);
            AppRuntime.AppendLifetimeLog($"item-failed {args.Error.ErrorCode} {args.Error.ExtendedError?.HResult:X8} {args.Error.ExtendedError?.Message} ({track?.Path})");
            OnUi(() => PlaybackFailed?.Invoke(track?.Path ?? ""));
        }

        private void List_CurrentItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
        {
            // 1 曲リピート: 曲が最後まで再生されて次へ進んだら、元の曲へ戻す
            if (_settings.Repeat == RepeatMode.One
                && args.Reason == MediaPlaybackItemChangedReason.EndOfStream
                && args.OldItem != null)
            {
                int oldIndex = sender.Items.IndexOf(args.OldItem);
                if (oldIndex >= 0)
                {
                    sender.MoveTo((uint)oldIndex);
                    return;
                }
            }

            var newItem = args.NewItem;
            OnUi(() =>
            {
                if (!ReferenceEquals(sender, _list))
                    return;

                CurrentTrack = newItem == null ? null : TrackOf(newItem);
                CurrentTrackChanged?.Invoke();
                if (newItem != null && CurrentTrack != null)
                    _ = ApplyThumbnailAsync(newItem, CurrentTrack);
            });
        }

        private IEnumerable<MediaPlaybackItem> OrderedItems() =>
            _list.ShuffleEnabled ? _list.ShuffledItems : _list.Items;

        private static int IdOf(MediaPlaybackItem item) =>
            item.Source.CustomProperties.TryGetValue(IdKey, out object? value) && value is int id ? id : -1;

        private Track? TrackOf(MediaPlaybackItem item) =>
            _tracksById.TryGetValue(IdOf(item), out var track) ? track : null;

        /// <summary>MediaPlayer のイベントは別スレッドから来るので UI スレッドへ移す。</summary>
        private void OnUi(Action action) => _ui.TryEnqueue(() => action());
    }
}
