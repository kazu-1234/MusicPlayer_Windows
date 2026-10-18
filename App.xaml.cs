using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUiShared;

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

            // アップデートの確認先は AppSetup.Profile.LatestReleaseApiUrl

            var settings = Settings.Load();
            // ライブラリの読み込み（並べ替え）より前に、名前の並べ方の設定を反映しておく
            Services.SortService.Mode = settings.NameSortMode;
            // 並べ替えに名前の並べ方の記録が無い（古い設定ファイル）なら、今の設定で埋めておく
            foreach (var spec in new[] { settings.SongsSort, settings.AlbumsSort, settings.ArtistsSort })
                spec.NameMode ??= settings.NameSortMode;

            // エクスプローラーのダブルクリック（既定のアプリ）で起動されると、引数に音声ファイルのパスが入る
            var openFiles = GetAudioFileArgs();

            // 開くファイルがあるときは、パスを書いてから表示依頼を送る（先に起こすと、相手がまだ空のファイルを読んでしまう）
            if (!SingleInstance.TryBecomePrimaryInstance(
                    requestInteractiveShow: openFiles.Count == 0,
                    onBecamePrimary: OpenRequestChannel.ClearStale))
            {
                if (openFiles.Count > 0)
                    OpenRequestChannel.Signal(openFiles);
                // 既に起動中のインスタンスへ依頼を送ったので、このプロセスは何もせず終了する
                Exit();
                return;
            }

            _runtime = new AppRuntime(this, settings);
            _runtime.Start(openFiles);
        }

        /// <summary>
        /// コマンドライン引数から、存在する音声ファイルのパスだけを取り出す。
        /// 非パッケージアプリでは LaunchActivatedEventArgs.Arguments が空になることがあるため、Environment から読む
        /// （先頭の要素は exe 自身のパスなので飛ばす）。
        /// </summary>
        private static List<string> GetAudioFileArgs() =>
            Environment.GetCommandLineArgs()
                .Skip(1)
                .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                .Where(a => Services.LibraryService.IsAudioFile(a) && File.Exists(a))
                .ToList();
    }
}
