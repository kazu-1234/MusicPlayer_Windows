using System;
using WinUiShared;

namespace MusicPlayer
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // --exit（インストーラからの終了依頼）と WinUI の起動は共通処理（WinUiShared.AppBootstrap）。
            // 開くファイルの引数は App.OnLaunched で扱う（二重起動の判定が必要なため）
            AppBootstrap.RunMain(args, AppSetup.Profile, handlePreLaunchArgs: null, () => new App());
        }
    }
}
