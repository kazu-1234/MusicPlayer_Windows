using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Views;

namespace MusicPlayer.Controls
{
    /// <summary>
    /// 画面下部に常に表示する再生バー。PlaybackService のイベントで表示を更新し、
    /// 再生位置だけはタイマーで定期的に読み直す（MediaPlayer は位置変化のイベントを出さないため）。
    /// </summary>
    public sealed partial class PlayerBar : UserControl
    {
        private PlaybackService? _playback;
        private readonly DispatcherTimer _positionTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(AppConfig.PositionUpdateIntervalMs)
        };

        /// <summary>タイマーがスライダーを動かしている最中（ユーザー操作によるシークと区別する）。</summary>
        private bool _updatingSlider;

        /// <summary>つまみをドラッグ中（離したときにまとめてシークする）。</summary>
        private bool _seekDragging;

        /// <summary>ミュート前の音量（ミュート解除で戻す）。</summary>
        private double _volumeBeforeMute = AppConfig.DefaultVolume;

        private bool _initializing;

        /// <summary>左側の曲情報がクリックされた（MainWindow が再生中画面へ移動する）。</summary>
        public event EventHandler? NowPlayingRequested;

        public PlayerBar()
        {
            InitializeComponent();
            _positionTimer.Tick += (_, _) => UpdatePosition();

            A11y.Label(ShuffleButton, Strings.Get("Player_Shuffle"));
            A11y.Label(PreviousButton, Strings.Get("Player_Previous"));
            A11y.Label(PlayPauseButton, Strings.Get("Player_PlayPause"));
            A11y.Label(NextButton, Strings.Get("Player_Next"));
            A11y.Label(MuteButton, Strings.Get("Player_Mute"));
            A11y.Label(NowPlayingButton, Strings.Get("Player_OpenNowPlaying"));
            ApplySizes();

            // Slider は内部でポインター操作を処理済み（Handled）にするため、handledEventsToo で受け取る
            SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _seekDragging = true), true);
            SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
            SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        }

        /// <summary>
        /// Windows 11 メディア プレーヤーの下部バーと同じ寸法にする。
        /// 数値は AppConfig に集めてあり、XAML に直書きしないことで後から 1 か所で調整できる。
        /// </summary>
        private void ApplySizes()
        {
            // 高さは MinHeight にする（拡大表示などで中身が大きくなったときに切れないように）
            RootPanel.MinHeight = AppConfig.PlayerBarHeight;

            ArtHost.Width = ArtHost.Height = AppConfig.PlayerBarArtSize;
            var corner = new CornerRadius(AppConfig.PlayerBarArtCornerRadius);
            ArtPlaceholder.CornerRadius = corner;
            ArtClip.CornerRadius = corner;

            // ジャケットをシークバーの時間表示より左（ウィンドウ端から PlayerBarArtLeftMargin）に寄せる。
            // 外枠の左余白（RootPanel.Padding.Left）の分だけ負の余白で戻す
            NowPlayingButton.Margin = new Thickness(AppConfig.PlayerBarArtLeftMargin - RootPanel.Padding.Left, 0, 0, 0);
            TrackInfoPanel.Spacing = AppConfig.PlayerBarArtTextSpacing;
            TrackTextPanel.MaxWidth = AppConfig.PlayerBarTextMaxWidth;

            TitleText.FontSize = AppConfig.PlayerBarTitleFontSize;
            ArtistText.FontSize = AppConfig.PlayerBarArtistFontSize;

            PlayPauseButton.Width = PlayPauseButton.Height = AppConfig.PlayerBarPlayButtonSize;
            PlayPauseButton.CornerRadius = new CornerRadius(AppConfig.PlayerBarPlayButtonSize / 2);
            foreach (Control button in new Control[] { ShuffleButton, PreviousButton, NextButton, RepeatButton, MuteButton })
            {
                button.Width = button.Height = AppConfig.PlayerBarIconButtonSize;
                button.CornerRadius = new CornerRadius(AppConfig.PlayerBarIconButtonSize / 2);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _playback = App.Runtime.AppState.Playback;
            _playback.CurrentTrackChanged += UpdateTrack;
            _playback.PlaybackStateChanged += UpdatePlayState;
            _playback.ModesChanged += UpdateModes;

            _initializing = true;
            VolumeSlider.Value = _playback.Volume;
            _initializing = false;

            UpdateTrack();
            UpdatePlayState();
            UpdateModes();
            UpdateVolumeIcon();
            _positionTimer.Start();
        }

        /// <summary>ウィンドウを閉じたときにイベントを外す（古いウィンドウを参照し続けないため）。</summary>
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _positionTimer.Stop();
            if (_playback == null)
                return;

            _playback.CurrentTrackChanged -= UpdateTrack;
            _playback.PlaybackStateChanged -= UpdatePlayState;
            _playback.ModesChanged -= UpdateModes;
        }

        private void UpdateTrack() => SafeAsync.Run(() => UpdateTrackAsync(), null, "UpdateTrack");

        private async Task UpdateTrackAsync()
        {
            Track? track = _playback?.CurrentTrack;
            TitleText.Text = track?.DisplayTitle ?? "";
            // WMP と同じく 2 行目は「アーティスト • アルバム」
            ArtistText.Text = track == null ? "" : $"{track.DisplayArtist} • {track.DisplayAlbum}";
            SeekSlider.IsEnabled = track != null;
            ArtImage.Source = null;

            if (track == null)
                return;

            string? art = await ArtworkCache.GetOrCreateAsync(ArtworkCache.KeyFor(track.Path), track.Path);
            // 待っている間に次の曲へ進んでいたら古い画像は出さない
            if (art != null && ReferenceEquals(track, _playback?.CurrentTrack))
                ArtImage.Source = new BitmapImage(new Uri(art)) { DecodePixelWidth = 104 };
        }

        private void UpdatePlayState()
        {
            bool playing = _playback?.IsPlaying == true;
            PlayPauseIcon.Glyph = playing ? "" : "";
        }

        private void UpdateModes()
        {
            if (_playback == null)
                return;

            ShuffleButton.IsChecked = _playback.Shuffle;
            RepeatButton.IsChecked = _playback.Repeat != RepeatMode.Off;
            // 1 曲リピートは「1」付きのアイコン
            RepeatIcon.Glyph = _playback.Repeat == RepeatMode.One ? "" : "";
            A11y.Label(RepeatButton, Strings.Get(_playback.Repeat switch
            {
                RepeatMode.All => "Player_RepeatAll",
                RepeatMode.One => "Player_RepeatOne",
                _ => "Player_RepeatOff"
            }));
        }

        private void UpdatePosition()
        {
            if (_playback == null || _seekDragging)
                return;

            double duration = _playback.Duration.TotalSeconds;
            double position = Math.Min(_playback.Position.TotalSeconds, duration);

            _updatingSlider = true;
            SeekSlider.Maximum = Math.Max(1, duration);
            SeekSlider.Value = Math.Max(0, position);
            _updatingSlider = false;

            PositionText.Text = Track.FormatDuration(position);
            DurationText.Text = Track.FormatDuration(duration);
        }

        private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingSlider || _playback == null)
                return;

            PositionText.Text = Track.FormatDuration(e.NewValue);
            // ドラッグ中は離したときにシークする（ドラッグ中に何十回もシークすると音が途切れるため）
            if (!_seekDragging)
                _playback.Seek(TimeSpan.FromSeconds(e.NewValue));
        }

        private void EndSeekDrag()
        {
            if (!_seekDragging)
                return;

            _seekDragging = false;
            _playback?.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _playback?.PlayPause();

        private void PreviousButton_Click(object sender, RoutedEventArgs e) => _playback?.Previous();

        private void NextButton_Click(object sender, RoutedEventArgs e) => _playback?.Next();

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playback != null)
                _playback.Shuffle = !_playback.Shuffle;
        }

        /// <summary>リピートは オフ → 全曲 → 1 曲 → オフ の順に切り替える。</summary>
        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playback == null)
                return;

            _playback.Repeat = _playback.Repeat switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off
            };
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_initializing || _playback == null)
                return;

            _playback.Volume = e.NewValue;
            UpdateVolumeIcon();
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (VolumeSlider.Value > 0)
            {
                _volumeBeforeMute = VolumeSlider.Value;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : AppConfig.DefaultVolume;
            }
        }

        private void UpdateVolumeIcon()
        {
            double v = VolumeSlider.Value;
            // 音量に応じてスピーカーの波の数を変える（0 はミュートのアイコン）
            VolumeIcon.Glyph = v <= 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        }

        /// <summary>
        /// 音量の上でホイールを回したら、AppConfig.VolumeWheelStep ずつ上げ下げする。
        /// 値はスライダーに入れるので、あとは VolumeSlider_ValueChanged（再生音量・アイコンの更新）に任せる。
        /// </summary>
        private void VolumePanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // MouseWheelDelta は 1 ノッチで ±120。向き（正＝奥へ回した＝上げる）だけを使う
            int delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
            if (delta == 0)
                return;

            double step = delta > 0 ? AppConfig.VolumeWheelStep : -AppConfig.VolumeWheelStep;
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, VolumeSlider.Minimum, VolumeSlider.Maximum);
            e.Handled = true; // ページ側がスクロールしないようにする
        }

        private void NowPlayingButton_Click(object sender, RoutedEventArgs e) =>
            NowPlayingRequested?.Invoke(this, EventArgs.Empty);
    }
}
