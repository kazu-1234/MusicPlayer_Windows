using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MusicPlayer.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using WinUiShared;

namespace MusicPlayer
{
    /// <summary>
    /// プロセス寿命（二重起動イベント・システム復帰・再生/ライブラリサービス）を管理する常駐オブジェクト。
    /// LHD 型: タスクトレイ・自動起動なし。MainWindow の × でプロセスごと終了する。
    /// </summary>
    public sealed class AppRuntime : IDisposable
    {
        private readonly Application _app;
        private readonly DispatcherQueue _uiDispatcher;
        private readonly Settings _settings;
        private readonly AppState _appState;

        private MainWindow? _mainWindow;
        private SystemEventWindow? _systemEventWindow;
        private ResumeReapplyCoordinator? _resumeCoordinator;
        private CancellationTokenSource? _listenerCts;
        private bool _systemEventInitialized;
        private bool _isExitingProcess;
        private bool _startupUpdateCheckScheduled;

        public AppRuntime(Application app, Settings settings)
        {
            _app = app;
            // 二重起動リスナーは BG スレッドから来るため、UI Dispatcher を起動時に保持する
            _uiDispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("AppRuntime must be created on the UI thread.");
            _settings = settings;

            // サービスはプロセスに 1 つ。ウィンドウを作り直しても再生・ライブラリは維持される
            var library = new LibraryService(settings, _uiDispatcher);
            var playback = new PlaybackService(_uiDispatcher, settings);
            var playlists = new PlaylistService(settings, library);
            var sync = new AndroidSyncService(settings, library, playlists);
            _appState = new AppState(settings, library, playback, playlists, sync);
        }

        public AppState AppState => _appState;
        public Settings Settings => _settings;
        public bool IsExitingProcess => _isExitingProcess;

        /// <summary>現在のメインウィンドウの ID（ファイル選択ダイアログの親に使う）。</summary>
        public Microsoft.UI.WindowId MainWindowId => _mainWindow?.AppWindow.Id ?? default;

        /// <summary>スリープ復帰・画面復帰・セッション解除など。</summary>
        public event Action? SystemResumed;

        /// <param name="openFiles">起動時のコマンドライン引数で渡された音声ファイル（エクスプローラーから開いたとき）。</param>
        public void Start(bool launchInBackground, bool requestInteractiveShow, IReadOnlyList<string>? openFiles = null)
        {
            ThemeService.Initialize(_settings.ThemePreference);
            StartListeners();
            EnsureResumeCoordinator();
            EnsureSystemEventMonitor();

            if (requestInteractiveShow || !launchInBackground)
                ShowOrCreateMainWindow();

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
            var timer = _uiDispatcher.CreateTimer();
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
                if (_isExitingProcess)
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
                if (!_isExitingProcess)
                    _appState.Playback.PlayTracks(tracks, 0, shuffle: false);
            }
            catch (Exception ex)
            {
                AppendLifetimeLog($"open-files-failed {ex}");
            }
        }

        /// <summary>2 つ目のプロセスからの表示依頼。開くファイルが添えられていれば、それも再生する（BG スレッドから呼ばれる）。</summary>
        private void OnShowRequested()
        {
            var files = SingleInstanceManager.TryConsumeOpenRequest();
            ShowOrCreateMainWindow();
            if (files.Count > 0)
                _uiDispatcher.TryEnqueue(() => QueueOpenFiles(files));
        }

        /// <summary>既存の MainWindow を前面表示、なければ生成して表示する。</summary>
        public void ShowOrCreateMainWindow(string? pageTag = null)
        {
            if (_isExitingProcess)
                return;

            _uiDispatcher.TryEnqueue(() => ShowOrCreateMainWindowCore(pageTag));
        }

        private void ShowOrCreateMainWindowCore(string? pageTag = null)
        {
            if (_isExitingProcess)
                return;

            if (_mainWindow != null)
            {
                BringWindowToForeground(_mainWindow);
                if (pageTag != null)
                    _mainWindow.NavigateToPageTag(pageTag);
                return;
            }

            _mainWindow = new MainWindow(this);
            _mainWindow.Closed += MainWindow_Closed;
            _mainWindow.PrepareAndActivate(pageTag);
            ScheduleStartupUpdateCheckIfNeeded();
        }

        private void ScheduleStartupUpdateCheckIfNeeded()
        {
            if (_startupUpdateCheckScheduled || _mainWindow == null)
                return;

            _startupUpdateCheckScheduled = true;
            _ = UpdateFlowService.TryStartupCheckAsync(_mainWindow, _settings);
        }

        /// <summary>MainWindow の × クローズ時に呼ばれる。ウィンドウ位置を保存する（終了は Closed 後）。</summary>
        public void OnMainWindowClosing(MainWindow window)
        {
            if (_isExitingProcess || window != _mainWindow)
                return;

            window.SaveWindowBoundsFromRuntime();
        }

        /// <summary>アプリを完全終了する唯一の入口（× / --exit / 更新インストール）。</summary>
        public void ExitApplication(string reason = "unknown")
        {
            if (_isExitingProcess)
                return;

            _isExitingProcess = true;
            AppendLifetimeLog(reason);
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;

            _resumeCoordinator?.Dispose();
            _resumeCoordinator = null;

            _systemEventWindow?.Dispose();
            _systemEventWindow = null;

            try { _appState.Playback.Dispose(); } catch { }
            try { _appState.Sync.Dispose(); } catch { }
            // 音量など、操作のたびには保存していない値をここで書き出す
            _settings.Save();

            SingleInstanceManager.Release();

            if (_mainWindow != null)
            {
                try { _mainWindow.Close(); } catch { }
                _mainWindow = null;
            }

            _app.Exit();
        }

        public void Dispose() => ExitApplication("dispose");

        /// <summary>LHD 型: ウィンドウが閉じたらプロセスも終了する。</summary>
        private void MainWindow_Closed(object sender, WindowEventArgs e)
        {
            if (!ReferenceEquals(_mainWindow, sender))
                return;

            _mainWindow = null;
            if (!_isExitingProcess)
                ExitApplication("window-close");
        }

        private void EnsureResumeCoordinator()
        {
            if (_resumeCoordinator != null)
                return;

            _resumeCoordinator = new ResumeReapplyCoordinator(
                _uiDispatcher,
                () => _isExitingProcess,
                OnResumeApply,
                OnResumeApplyDirect);
            _resumeCoordinator.Start();
        }

        /// <summary>復帰監視窓。失敗時はフラグを戻し再試行可能にする（SPM/BlueShift 同型）。</summary>
        private void EnsureSystemEventMonitor()
        {
            if (_systemEventInitialized)
            {
                if (_systemEventWindow is { IsAlive: true })
                    return;

                _systemEventWindow?.Dispose();
                _systemEventWindow = null;
                _systemEventInitialized = false;
            }

            try
            {
                EnsureResumeCoordinator();
                _systemEventWindow = new SystemEventWindow("MusicPlayerSystemEventWindow_v1");
                _systemEventWindow.SystemDisplayStateChanged += () => _resumeCoordinator?.NotifyResume();
                _systemEventWindow.SystemSuspending += () => _resumeCoordinator?.NotifySuspend();
                _systemEventInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"System event monitor init failed: {ex.Message}");
                _systemEventWindow?.Dispose();
                _systemEventWindow = null;
                _systemEventInitialized = false;
            }
        }

        private void OnResumeApply()
        {
            try
            {
                EnsureSystemEventMonitor();
                SystemResumed?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SystemResumed failed: {ex.Message}");
            }
        }

        private void OnResumeApplyDirect()
        {
            try { SystemResumed?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"SystemResumed fallback failed: {ex.Message}"); }
        }

        private void StartListeners()
        {
            var showEvent = SingleInstanceManager.InteractiveShowEvent;
            var exitEvent = SingleInstanceManager.ExitEvent;
            if (showEvent == null && exitEvent == null)
                return;

            _listenerCts = new CancellationTokenSource();
            var token = _listenerCts.Token;

            if (showEvent != null)
                Task.Run(() => ListenShowLoop(showEvent, token, OnShowRequested), token);

            if (exitEvent != null)
                Task.Run(() => ListenLoop(exitEvent, token, () => _uiDispatcher.TryEnqueue(() => ExitApplication("exit-signal"))), token);
        }

        private static void ListenShowLoop(EventWaitHandle handle, CancellationToken token, Action action)
        {
            while (!token.IsCancellationRequested)
            {
                bool signaled = false;
                try
                {
                    signaled = handle.WaitOne(500);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (!signaled)
                    signaled = SingleInstanceManager.TryConsumeShowSignal();

                if (token.IsCancellationRequested)
                    break;

                if (signaled)
                    action();
            }
        }

        private static void ListenLoop(EventWaitHandle handle, CancellationToken token, Action action)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    handle.WaitOne(500);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                if (SingleInstanceManager.TryConsumeExitSignal())
                    action();
            }
        }

        private static void BringWindowToForeground(Window window)
        {
            try
            {
                if (window.AppWindow.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }

                window.AppWindow.IsShownInSwitchers = true;
                window.AppWindow.Show();
                window.Activate();

                IntPtr hwnd = WindowNative.GetWindowHandle(window);
                if (hwnd != IntPtr.Zero)
                    SetForegroundWindow(hwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BringWindowToForeground failed: {ex.Message}");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        internal static void AppendLifetimeLog(string reason) =>
            LifetimeLog.Append(AppConfig.AppId, reason);
    }
}
