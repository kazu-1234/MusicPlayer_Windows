using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 音楽ファイルを開くアプリとして Windows に登録する（HKCU = このユーザーだけ。管理者権限は不要）。
    /// <para>
    /// Windows 10 / 11 では、アプリが自分で「既定のアプリ」になることはできない（UserChoice はハッシュで保護されている）。
    /// そこで「開けるアプリの候補」として登録だけ行い、最後は Windows の既定のアプリ画面でユーザーに選んでもらう。
    /// アンインストール時はインストーラ（MusicPlayer.iss）が同じキーを消す。
    /// </para>
    /// </summary>
    public static class FileAssociationService
    {
        /// <summary>ファイルの種類の ID（ProgID）。拡張子 → この ID → 開くコマンド、の順に Windows が引く。</summary>
        public const string ProgId = AppConfig.AppId + ".AudioFile";

        /// <summary>「既定のアプリ」画面に出すための機能（Capabilities）の置き場。</summary>
        private const string CapabilitiesKey = @"Software\" + AppConfig.AppId + @"\Capabilities";

        /// <summary>
        /// 登録して、Windows の既定のアプリ画面（このアプリのページ）を開く。
        /// </summary>
        public static void RegisterAndOpenSettings()
        {
            Register();
            // registeredAppUser = RegisteredApplications に書いた名前。このアプリの拡張子一覧のページが直接開く
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppConfig.AppId}") { UseShellExecute = true });
        }

        private static void Register()
        {
            // 実行中の exe のパス（インストール先でも開発中のビルドでも、今動いている exe を登録する）
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("exe path not found");
            string command = $"\"{exe}\" \"%1\"";
            string appName = Strings.Get("AppName");

            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            // 1) ProgID: アイコンと「開く」コマンド。%1 に開くファイルのパスが入る
            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue("", appName);
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{exe}\",0");
                using (var open = prog.CreateSubKey(@"shell\open\command"))
                    open.SetValue("", command);
            }

            // 2) 各拡張子の「プログラムから開く」の候補にこの ProgID を足す（既存の既定アプリは上書きしない）
            foreach (string ext in AppConfig.AudioExtensions)
            {
                using var openWith = classes.CreateSubKey($@"{ext}\OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            // 3) Capabilities + RegisteredApplications: これがあると設定 → 既定のアプリ の一覧にアプリ名で出る
            using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
            {
                caps.SetValue("ApplicationName", appName);
                caps.SetValue("ApplicationDescription", appName);
                using var assoc = caps.CreateSubKey("FileAssociations");
                foreach (string ext in AppConfig.AudioExtensions)
                    assoc.SetValue(ext, ProgId);
            }
            using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                registered.SetValue(AppConfig.AppId, CapabilitiesKey);

            // エクスプローラーに「関連付けが変わった」と知らせる（アイコンや「プログラムから開く」をすぐ更新させる）
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
