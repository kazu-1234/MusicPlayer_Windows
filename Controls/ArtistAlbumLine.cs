using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Models;

namespace MusicPlayer.Controls
{
    /// <summary>
    /// 「アーティスト • アルバム」の 1 行（再生バーと再生中画面で共通）。アーティスト名・アルバム名はそれぞれ押せる。
    /// <para>
    /// 列は「アーティスト（Auto）／ • （Auto）／アルバム（残り全部）」。長い名前は「…」で省略する。
    /// アーティストが長すぎてアルバムが見えなくならないよう、アーティストの幅は全体の AppConfig.ArtistLinkMaxWidthRatio までにする。
    /// </para>
    /// </summary>
    public sealed class ArtistAlbumLine : Grid
    {
        private readonly LinkText _artist = new();
        private readonly TextBlock _separator = new() { Text = " • " };
        private readonly LinkText _album = new();
        private Track? _track;

        public ArtistAlbumLine()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SetColumn(_separator, 1);
            SetColumn(_album, 2);
            Children.Add(_artist);
            Children.Add(_separator);
            Children.Add(_album);

            // 文字の色・省略の仕方は一覧の 2 列目と同じ（薄い色・「…」で省略）
            var style = (Style)Application.Current.Resources["ListSecondaryTextStyle"];
            _artist.TextStyle = style;
            _album.TextStyle = style;
            _separator.Style = style;

            _artist.Click += (_, _) => { if (_track != null) ArtistClick?.Invoke(_track); };
            _album.Click += (_, _) => { if (_track != null) AlbumClick?.Invoke(_track); };
            Visibility = Visibility.Collapsed;
        }

        /// <summary>アーティスト名が押された（引数は表示中の曲）。</summary>
        public event Action<Track>? ArtistClick;

        /// <summary>アルバム名が押された（引数は表示中の曲）。</summary>
        public event Action<Track>? AlbumClick;

        /// <summary>表示する曲。null なら行ごと隠す。</summary>
        public Track? Track
        {
            get => _track;
            set
            {
                _track = value;
                _artist.Text = value?.DisplayArtist ?? "";
                _album.Text = value?.DisplayAlbum ?? "";
                Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>文字の大きさ（3 つの部品すべてに入れる）。</summary>
        public double FontSize
        {
            get => _separator.FontSize;
            set => _artist.FontSize = _album.FontSize = _separator.FontSize = value;
        }

        /// <summary>行全体の最大幅。アーティストの最大幅もここから決める。</summary>
        public double LineMaxWidth
        {
            get => MaxWidth;
            set
            {
                MaxWidth = value;
                _artist.MaxWidth = value * AppConfig.ArtistLinkMaxWidthRatio;
            }
        }
    }
}
