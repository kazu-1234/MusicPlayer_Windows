using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WinUiShared;

namespace MusicPlayer
{
    /// <summary>
    /// エクスプローラーから音声ファイルを開いたとき、2 つ目のプロセスが既存プロセスへ「このファイルを再生して」と渡す仕組み（Music 固有）。
    /// パスはファイルに 1 行 1 件で追記し、表示イベント（WinUiShared.SingleInstance）で既存プロセスを起こす。
    /// 受け取った側は表示ついでに <see cref="TryConsume"/> で読む。
    /// </summary>
    internal static class OpenRequestChannel
    {
        /// <summary>相手プロセスの読み書きを待つ上限（相手の処理は一瞬なので長くは待たない）。</summary>
        private const int LockWaitMs = 2000;

        /// <summary>2 つ目のプロセスが「このファイルを開いて」と頼むときに、パスを 1 行 1 件で書くファイル。</summary>
        private static string OpenRequestFilePath => Path.Combine(AppSetup.Profile.AppDataDirectory, ".open_request");

        /// <summary>.open_request の読み書きを、複数プロセス（複数選択で同時に起動される）の間で順番にするための名前付き Mutex。</summary>
        private static string OpenRequestMutexName => SingleInstance.BaseMutexName + "_OpenRequest";

        /// <summary>
        /// 前回が異常終了して残った「開く」依頼を捨てる（今回の起動と関係ない曲が再生されないように）。
        /// primary になった直後・表示イベントを作る前に呼ぶので、これから届く依頼を消してしまうことはない。
        /// </summary>
        public static void ClearStale()
        {
            try { File.Delete(OpenRequestFilePath); } catch { }
        }

        /// <summary>既存インスタンスへ「このファイルを再生して」と頼む（パスを書いてから表示依頼を送る）。</summary>
        public static void Signal(IReadOnlyList<string> paths)
        {
            try
            {
                Directory.CreateDirectory(AppSetup.Profile.AppDataDirectory);
                WithLock(() => File.AppendAllLines(OpenRequestFilePath, paths, new UTF8Encoding(false)));
            }
            catch
            {
            }

            // 先に起こすと、相手がまだ空のファイルを読んでしまうので、書いた後に起こす
            SingleInstance.SignalInteractiveShow();
        }

        /// <summary>開く依頼のパスがあれば読み取ってファイルを消す。無ければ空。</summary>
        public static IReadOnlyList<string> TryConsume()
        {
            if (!File.Exists(OpenRequestFilePath))
                return Array.Empty<string>();

            try
            {
                string[] lines = Array.Empty<string>();
                WithLock(() =>
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
        private static void WithLock(Action action)
        {
            using var mutex = new Mutex(false, OpenRequestMutexName);
            bool owned = false;
            try
            {
                // 取れなくても処理は進める（最悪でも 1 件取りこぼすだけ）
                try { owned = mutex.WaitOne(LockWaitMs); }
                catch (AbandonedMutexException) { owned = true; }
                action();
            }
            finally
            {
                if (owned)
                    mutex.ReleaseMutex();
            }
        }
    }
}
