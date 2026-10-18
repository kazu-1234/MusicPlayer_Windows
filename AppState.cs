using MusicPlayer.Services;

namespace MusicPlayer
{
    /// <summary>
    /// ページ間で共有するアプリ状態。サービスは AppRuntime が生成・所有し、
    /// ウィンドウを作り直しても同じインスタンスを使い続ける（再生が止まらない）。
    /// </summary>
    public sealed class AppState
    {
        public AppState(Settings settings, LibraryService library, PlaybackService playback, PlaylistService playlists,
            AndroidSyncService sync)
        {
            Settings = settings;
            Library = library;
            Playback = playback;
            Playlists = playlists;
            Sync = sync;
        }

        public Settings Settings { get; }
        public LibraryService Library { get; }
        public PlaybackService Playback { get; }
        public PlaylistService Playlists { get; }

        /// <summary>Android 版への同期（PC → Android）。</summary>
        public AndroidSyncService Sync { get; }
    }
}
