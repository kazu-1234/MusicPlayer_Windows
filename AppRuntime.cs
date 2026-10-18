using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MusicPlayer.Services;
using System;
using System.Diagnostics;
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
            _appState = new AppState(settings, library, playback, playlists);
        }

        public AppState AppState => _appState;
        public Settings Settings => _settings;
        public bool IsExitingProcess => _isExitingProcess;

        /// <summary>現在のメインウィンドウの ID（ファイル選択ダイアログの親に使う）。</summary>
        public Microsoft.UI.WindowId MainWindowId => _mainWindow?.AppWindow.Id ?? default;

        /// <summary>スリープ復帰・画面復帰・セッション解除など。</summary>
        public event Action? SystemResumed;

        public void Start(bool launchInBackground, bool requestInteractiveShow)
        {
            ThemeService.Initialize(_settings.ThemePreference);
            StartListeners();
            EnsureResumeCoordinator();
            EnsureSystemEventMonitor();

            if (requestInteractiveShow || !launchInBackground)
                ShowOrCreateMainWindow();

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
                await _appState.Library.ScanAsync();
            }
            catch (Exception ex)
            {
                AppendLifetimeLog($"library-scan-failed {ex.GetType().Name}: {ex.Message}");
            }
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
                Task.Run(() => ListenShowLoop(showEvent, token, () => ShowOrCreateMainWindow()), token);

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
