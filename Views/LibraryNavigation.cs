using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Models;

namespace MusicPlayer.Views
{
    /// <summary>
    /// アーティスト名・アルバム名を押したときに、そのページへ移動する（再生バー・再生中画面・各曲一覧で共通）。
    /// ライブラリに無い曲（プレイリストのライブラリ外の曲など）はページが無いので何もしない。
    /// </summary>
    public static class LibraryNavigation
    {
        public static void GoToArtist(Frame frame, Track track) =>
            GoTo(frame, App.Runtime.AppState.Library.ArtistOf(track));

        /// <summary>名前でアーティストへ（アルバム詳細のヘッダーなど、曲ではなく名前しか無い場所用）。</summary>
        public static void GoToArtist(Frame frame, string name) =>
            GoTo(frame, App.Runtime.AppState.Library.FindArtist(name));

        public static void GoToAlbum(Frame frame, Track track)
        {
            var album = App.Runtime.AppState.Library.AlbumOf(track);
            // すでにそのアルバムを表示中なら移動しない（戻る履歴に同じページを積まないため）
            if (album == null || (frame.Content is AlbumDetailPage page && page.CurrentAlbum == album))
                return;
            frame.Navigate(typeof(AlbumDetailPage), album);
        }

        private static void GoTo(Frame frame, Artist? artist)
        {
            if (artist == null || (frame.Content is ArtistDetailPage page && page.CurrentArtist == artist))
                return;
            frame.Navigate(typeof(ArtistDetailPage), artist);
        }
    }
}
