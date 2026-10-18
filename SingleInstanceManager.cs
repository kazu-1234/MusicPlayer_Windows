using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MusicPlayer
{
    /// <summary>
    /// 二重起動防止と、既存インスタンスへの表示／終了依頼（Mutex + 名前付きイベント）。
    /// </summary>
    internal static class SingleInstanceManager
    {
#if DEBUG
        private const string MutexName = "Global\\MusicPlayer_SingleInstance_v1_DEBUG";
        private const string InteractiveShowEventName = "Global\\MusicPlayer_ShowInteractive_v1_DEBUG";
        private const string ExitEventName = "Global\\MusicPlayer_Exit_v1_DEBUG";
#else
        private const string MutexName = "Global\\MusicPlayer_SingleInstance_v1";
        private const string InteractiveShowEventName = "Global\\MusicPlayer_ShowInteractive_v1";
        private const string ExitEventName = "Global\\MusicPlayer_Exit_v1";
#endif
        // 新しいプロジェクトへ複製する際は、他アプリと衝突しないよう上記の名前（AppName 部分）を変更すること。

        private static Mutex? _mutex;
        private static EventWaitHandle? _interactiveShowEvent;
        private static EventWaitHandle? _exitEvent;

        private static string AppDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPlayer");

        private static string PidFilePath => Path.Combine(AppDataDirectory, ".instance_pid");

        private static string SignalFilePath => Path.Combine(AppDataDirectory, ".show_signal");

        private static string ExitSignalFilePath => Path.Combine(AppDataDirectory, ".exit_signal");

        /// <summary>2 つ目のプロセスが「このファイルを開いて」と頼むときに、パスを 1 行 1 件で書くファイル。</summary>
        private static string OpenRequestFilePath => Path.Combine(AppDataDirectory, ".open_request");

        /// <summary>.open_request の読み書きを、複数プロセス（複数選択で同時に起動される）の間で順番にするための名前付き Mutex。</summary>
        private const string OpenRequestMutexName = MutexName + "_OpenRequest";

        public static EventWaitHandle? InteractiveShowEvent => _interactiveShowEvent;
        public static EventWaitHandle? ExitEvent => _exitEvent;

        /// <param name="requestInteractiveShow">
        /// true のとき、既存インスタンスへ「ユーザー操作で GUI を開く」ことを通知する。
        /// --background の二重起動では false（通知しない）。
        /// </param>
        public static bool TryBecomePrimaryInstance(bool requestInteractiveShow)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                if (requestInteractiveShow)
                    SignalInteractiveShow();

                return false;
            }

            // 前回が異常終了して残った「開く」依頼は捨てる（今回の起動と関係ない曲が再生されないように）。
            // イベントを作る前に消すので、これから届く依頼を消してしまうことはない
            try { File.Delete(OpenRequestFilePath); } catch { }

            _interactiveShowEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                InteractiveShowEventName);
            _exitEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ExitEventName);

            TryWritePidFile();
            return true;
        }

        public static void SignalInteractiveShow()
        {
            TryAllowForegroundForPrimary();

            bool signaled = false;
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(InteractiveShowEventName);
                showEvent.Set();
                signaled = true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            // Event 失敗時のフォールバック（成功時は二重表示を避ける）
            if (signaled)
                return;

            try
            {
                Directory.CreateDirectory(AppDataDirectory);
                File.WriteAllText(SignalFilePath, DateTime.UtcNow.ToString("O"));
            }
            catch
            {
            }
        }

        /// <summary>既存インスタンスへ終了を依頼する（インストーラ / --exit 用）。ファイルが本体、イベントは起床用。</summary>
        public static void SignalExit()
        {
            try
            {
                Directory.CreateDirectory(AppDataDirectory);
                File.WriteAllText(ExitSignalFilePath, DateTime.UtcNow.ToString("O"));
            }
            catch
            {
            }

            try
            {
                using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
                exitEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
        }

        /// <summary>
        /// 既存インスタンスへ「このファイルを再生して」と頼む（エクスプローラーから開いたとき）。
        /// パスはファイルに追記し、表示イベントで起こす。受け取った側は表示ついでに TryConsumeOpenRequest で読む。
        /// </summary>
        public static void SignalOpenFiles(IReadOnlyList<string> paths)
        {
            try
            {
                Directory.CreateDirectory(AppDataDirectory);
                WithOpenRequestLock(() => File.AppendAllLines(OpenRequestFilePath, paths, new UTF8Encoding(false)));
            }
            catch
            {
            }

            SignalInteractiveShow();
        }

        /// <summary>開く依頼のパスがあれば読み取ってファイルを消す。無ければ空。</summary>
        public static IReadOnlyList<string> TryConsumeOpenRequest()
        {
            if (!File.Exists(OpenRequestFilePath))
                return Array.Empty<string>();

            try
            {
                string[] lines = Array.Empty<string>();
                WithOpenRequestLock(() =>
                {
                    lines = File.ReadAllLines(OpenRequestFilePath, Encoding.UTF8);
                    File.Delete(OpenRequestFilePath);
                });
                return lines.Where(l => l.Length > 0).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>書き込み途中のファイルを読んだり、2 つのプロセスが同時に追記したりしないよう Mutex で囲む。</summary>
        private static void WithOpenRequestLock(Action action)
        {
            using var mutex = new Mutex(false, OpenRequestMutexName);
            bool owned = false;
            try
            {
                // 相手の処理は一瞬なので長くは待たない。取れなくても処理は進める（最悪でも 1 件取りこぼすだけ）
                try { owned = mutex.WaitOne(2000); }
                catch (AbandonedMutexException) { owned = true; }
                action();
            }
            finally
            {
                if (owned)
                    mutex.ReleaseMutex();
            }
        }

        /// <summary>ファイル信号があれば消費して true。</summary>
        public static bool TryConsumeShowSignal()
        {
            return TryConsumeFile(SignalFilePath);
        }

        /// <summary>終了握手ファイルがあれば消費して true。イベントだけの起床は無視する。</summary>
        public static bool TryConsumeExitSignal()
        {
            return TryConsumeFile(ExitSignalFilePath);
        }

        private static bool TryConsumeFile(string path)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Release()
        {
            TryDeletePidFile();

            _interactiveShowEvent?.Dispose();
            _interactiveShowEvent = null;
            _exitEvent?.Dispose();
            _exitEvent = null;

            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
                _mutex = null;
            }
        }

        private static void TryWritePidFile()
        {
            try
            {
                Directory.CreateDirectory(AppDataDirectory);
                File.WriteAllText(PidFilePath, Environment.ProcessId.ToString());
            }
            catch
            {
            }
        }

        private static void TryDeletePidFile()
        {
            try
            {
                if (File.Exists(PidFilePath))
                    File.Delete(PidFilePath);
            }
            catch
            {
            }
        }

        private static void TryAllowForegroundForPrimary()
        {
            try
            {
                if (!File.Exists(PidFilePath))
                    return;

                if (!int.TryParse(File.ReadAllText(PidFilePath).Trim(), out int pid))
                    return;

                AllowSetForegroundWindow(pid);
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}
