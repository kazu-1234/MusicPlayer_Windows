using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Threading;

namespace MusicPlayer
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // インストーラ等から既存プロセスへ終了依頼（WinUI を起動しない）。
            // AppRuntime.ExitApplication → Application.Exit() 以外の終了経路は用意しない。
            if (HasArg(args, "--exit"))
            {
                SingleInstanceManager.SignalExit();
                Thread.Sleep(1500);
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

        private static bool HasArg(string[] args, string arg)
        {
            foreach (string item in args)
            {
                if (string.Equals(item, arg, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
