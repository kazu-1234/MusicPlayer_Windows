using Microsoft.UI.Xaml;
using MusicPlayer.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinUiShared;

namespace MusicPlayer
{
    /// <summary>
    /// プロセス寿命（二重起動イベント・システム復帰・再生/ライブラリサービス）を管理する常駐オブジェクト。
    /// 共通処理は WinUiShared.AppRuntimeBase。ウィンドウ型（AppSetup.Profile）なので
    /// タスクトレイ・自動起動なし、MainWindow の × でプロセスごと終了する。復帰監視は使う（SystemResumed）。
    /// </summary>
    public sealed class AppRuntime : AppRuntimeBase
    {
        private readonly Settings _settings;
        private readonly AppState _appState;

        public AppRuntime(Application app, Settings settings)
            : base(app, settings)
        {
            _settings = settings;

            // サービスはプロセスに 1 つ。ウィンドウを作り直しても再生・ライブラリは維持される
            var library = new LibraryService(settings, UiDispatcher);
            var playback = new PlaybackService(UiDispatcher, settings);
            var playlists = new PlaylistService(settings, library);
            var sync = new AndroidSyncService(settings, library, playlists);
            _appState = new AppState(settings, library, playback, playlists, sync);
        }

        public AppState AppState => _appState;
        public Settings Settings => _settings;

        /// <summary>現在のメインウィンドウの ID（ファイル選択ダイアログの親に使う）。</summary>
        public Microsoft.UI.WindowId MainWindowId => CurrentMainWindow?.AppWindow.Id ?? default;

        protected override Window CreateMainWindow() => new MainWindow(this);

        /// <param name="openFiles">起動時のコマンドライン引数で渡された音声ファイル（エクスプローラーから開いたとき）。</param>
        public void Start(IReadOnlyList<string>? openFiles)
        {
            // 共通の起動（テーマ・二重起動リスナー・復帰監視・MainWindow 表示）。ウィンドウ型なので常に窓を出す
            Start(launchInBackground: false, requestInteractiveShow: true);

            if (openFiles is { Count: > 0 })
                QueueOpenFiles(openFiles);

            _ = StartLibraryAsync();
        }

        /// <summary>キャッシュで即表示 → バックグラウンドで差分スキャン、の順に起動する。</summary>
        private async Task StartLibraryAsync()
        {
            try
            {
                // ライブラリ内に「Playlists」フォルダがあれば、そこをプレイリスト用フォルダにする
                _appState.Playlists.ApplyDetectedFolder();
                await _appState.Library.LoadCacheAsync();
            }
            catch (Exception ex)
            {
                AppendLifetimeLog($"library-cache-failed {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // キャッシュを読み終えたら（失敗しても）、待っていた「ファイルを開く」を進めてよい
                _libraryCacheLoaded.TrySetResult();
            }

            try
            {
                await _appState.Library.ScanAsync();
            }
            catch (Exception ex)
            {
                AppendLifetimeLog($"library-scan-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ───────── エクスプローラーから開いたファイルの再生 ─────────

        /// <summary>
        /// ライブラリのキャッシュを読み終えたら完了する。開いたファイルがライブラリの曲かどうかは、これを待ってから判定する
        /// （スキャン完了までは待たない。キャッシュがあれば一瞬で、初回起動のようにキャッシュが無ければライブラリ外として再生する）。
        /// </summary>
        private readonly TaskCompletionSource _libraryCacheLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>まとめて処理するまで溜めている「開く」依頼のパス（UI スレッドだけで触る）。</summary>
        private readonly List<string> _pendingOpenFiles = new();
        private bool _openFlushScheduled;

        /// <summary>
        /// 開く依頼を溜める。エクスプローラーで複数選択して開くと 1 ファイルごとに別プロセスが起動して 1 件ずつ届くので、
        /// AppConfig.OpenFilesCollectMs だけ待ってから 1 つのキューにまとめて再生する。UI スレッドから呼ぶ。
        /// </summary>
        public void QueueOpenFiles(IReadOnlyList<string> paths)
        {
            _pendingOpenFiles.AddRange(paths);
            if (_openFlushScheduled)
                return;

            _openFlushScheduled = true;
            var timer = UiDispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(AppConfig.OpenFilesCollectMs);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                var files = _pendingOpenFiles.ToList();
                _pendingOpenFiles.Clear();
                _openFlushScheduled = false;
                _ = OpenFilesAsync(files);
            };
            timer.Start();
        }

        /// <summary>
        /// 開いたファイルを再生する。
        /// - 1 曲だけで、それがライブラリの曲 → その曲を含むアルバムを、その曲から通常再生（シャッフルなし）
        /// - ライブラリ外の曲や複数の曲 → 渡された順に、その曲だけを再生（ライブラリには追加しない）
        /// </summary>
        private async Task OpenFilesAsync(IReadOnlyList<string> paths)
        {
            try
            {
                await _libraryCacheLoaded.Task;
                if (IsExitingProcess)
                    return;

                var library = _appState.Library;
                // 同じファイルが二重に届いても 1 回だけ。存在しない・音声でないものは除く
                var files = paths.Select(p => System.IO.Path.GetFullPath(p.Trim('"')))
                                 .Where(p => System.IO.File.Exists(p) && LibraryService.IsAudioFile(p))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();
                if (files.Count == 0)
                    return;

                if (files.Count == 1 && library.FindByPath(files[0]) is { } known)
                {
                    // アルバムは「アーティスト|アルバム|形式」でまとめてあるので、曲のパスから引き当て表で探す
                    var album = library.AlbumOf(known);
                    if (album != null)
                    {
                        _appState.Playback.PlayTracks(album.Tracks, album.Tracks.IndexOf(known), shuffle: false);
                        return;
                    }
                }

                // ライブラリ外の曲はタグを読む必要がある（ディスク I/O なので UI スレッドを止めないよう別スレッドで）
                var tracks = await Task.Run(() => files.Select(library.GetOrRead).ToList());
                if (!IsExitingProcess)
                    _appState.Playback.PlayTracks(tracks, 0, shuffle: false);
            }
            catch (Exception ex)
            {
                AppendLifetimeLog($"open-files-failed {ex}");
            }
        }

        /// <summary>2 つ目のプロセスからの表示依頼。開くファイルが添えられていれば、それも再生する（BG スレッドから呼ばれる）。</summary>
        protected override void OnInteractiveShowRequested()
        {
            var files = OpenRequestChannel.TryConsume();
            ShowOrCreateMainWindow();
            if (files.Count > 0)
                UiDispatcher.TryEnqueue(() => QueueOpenFiles(files));
        }

        /// <summary>終了時（窓を閉じた後）。再生・同期を止め、操作のたびには保存していない値（音量など）を書き出す。</summary>
        protected override void OnExitCore()
        {
            try { _appState.Playback.Dispose(); } catch { }
            try { _appState.Sync.Dispose(); } catch { }
            _settings.Save();
        }
    }
}
