using WinUiShared;

namespace MusicPlayer
{
    /// <summary>
    /// このアプリの「型」と識別名（共通基盤 WinUiShared に渡す値はすべてここに集約する）。
    /// MusicPlayer はウィンドウ型: トレイ・自動起動なし、× でプロセスごと終了する。
    /// スリープ復帰の監視は使う（AppRuntimeBase.SystemResumed）。
    /// </summary>
    internal static class AppSetup
    {
        public static readonly AppProfile Profile = new()
        {
            // %AppData%\MusicPlayer・Mutex 名などの内部名（AppConfig.AppId と同じ。表示名は resw の AppName）
            AppName = AppConfig.AppId,
            // 旧版と同じ Mutex / イベント名（Global\MusicPlayer_SingleInstance_v1）にして、インストーラの --exit を届かせる
            InstanceNameVersion = "v1",
            // 更新の確認先（GitHub の Releases の最新版）。タグ vX.Y.Z と *setup*.exe を見て更新を判断する
            LatestReleaseApiUrl = "https://api.github.com/repos/kazu-1234/MusicPlayer_Windows/releases/latest",

            CloseBehavior = WindowCloseBehavior.ExitProcess,
            UseTray = false,
            UseResumeMonitor = true,
            SyncAutostart = null,
            SystemEventWindowClassName = "MusicPlayerSystemEventWindow_v1",

            GetString = Strings.Get,
            FormatString = Strings.Format
        };
    }
}
