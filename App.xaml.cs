using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MusicPlayer
{
    public partial class App : Application
    {
        private AppRuntime? _runtime;

        internal static AppRuntime Runtime =>
            (Current as App)?._runtime
            ?? throw new InvalidOperationException("App runtime is not initialized.");

        public App()
        {
            InitializeComponent();

            // 最後の Window が閉じても自動終了させず、終了は AppRuntime.ExitApplication に一本化する
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            UnhandledException += App_UnhandledException;

            // XAML の UnhandledException はバックグラウンドスレッドの例外を拾わない。
            // スレッドプールで起きた例外（＝プロセスごと落ちる）も原因が後から分かるよう、ログに残す
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppRuntime.AppendLifetimeLog($"unhandled-domain {e.ExceptionObject}");

            // await されずに捨てられた Task の例外。SetObserved で「処理済み」にしてプロセス終了を防ぐ
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppRuntime.AppendLifetimeLog($"unobserved-task {e.Exception}");
                e.SetObserved();
            };
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine(e.Exception);
            // ToString() はスタックトレース付き。どこで落ちたかを特定するため型名・メッセージだけでなく全体を書く
            AppRuntime.AppendLifetimeLog($"unhandled {e.Exception}");
            e.Handled = true;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // XAML から AppConfig の定数を直接参照できないため、起動時にリソースへ書き込んでおく
            // （コンストラクターではまだ Resources を触れないので、最初のウィンドウを作る前のここで行う）
            try
            {
                Resources["TrackRowHeight"] = AppConfig.TrackRowHeight;
                Resources["TrackRowArtColumnWidth"] = new GridLength(AppConfig.TrackRowArtSize);
            }
            catch (Exception ex)
            {
                // 失敗しても App.xaml に書いた既定値で表示できる
                AppRuntime.AppendLifetimeLog($"resource-override-failed {ex}");
            }

            // アップデートの確認先（GitHub の Releases の最新版）。タグ vX.Y.Z と *setup*.exe を見て更新を判断する
            UpdateChecker.LatestReleaseApiUrl = "https://api.github.com/repos/kazu-1234/MusicPlayer_Windows/releases/latest";

            var settings = Settings.Load();
            // ライブラリの読み込み（並べ替え）より前に、名前の並べ方の設定を反映しておく
            Services.SortService.Mode = settings.NameSortMode;
            // 並べ替えに名前の並べ方の記録が無い（古い設定ファイル）なら、今の設定で埋めておく
            foreach (var spec in new[] { settings.SongsSort, settings.AlbumsSort, settings.ArtistsSort })
                spec.NameMode ??= settings.NameSortMode;

            if (!SingleInstanceManager.TryBecomePrimaryInstance(requestInteractiveShow: true))
            {
                // 既に起動中のインスタンスへ表示依頼を送ったので、このプロセスは何もせず終了する
                Exit();
                return;
            }

            _runtime = new AppRuntime(this, settings);
            _runtime.Start(launchInBackground: false, requestInteractiveShow: true);
        }
    }
}
