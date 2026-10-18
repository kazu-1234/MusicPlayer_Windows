using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MusicPlayer.Native
{
    /// <summary>
    /// タスクバーのプレビュー（アイコンにマウスを乗せたときの小窓）に「前へ／再生・一時停止／次へ」ボタンを出す。
    /// アプリを前面に出さなくても（最小化中でも）操作できるようにするため。WMP と同じ仕組み（ITaskbarList3）。
    /// <para>
    /// ボタンが押されると、ウィンドウに WM_COMMAND が届く。WinUI のウィンドウはこのメッセージをアプリに渡さないので、
    /// SetWindowSubclass でウィンドウのメッセージ処理に割り込んで受け取る。
    /// </para>
    /// </summary>
    public sealed class TaskbarThumbButtons : IDisposable
    {
        /// <summary>押されたボタン。</summary>
        public enum Button : uint { Previous = 1, PlayPause = 2, Next = 3 }

        private readonly IntPtr _hwnd;
        private readonly Action<Button> _onClick;
        private readonly SubclassProc _proc; // GC に回収されないよう、デリゲートをフィールドで持ち続ける
        private readonly uint _taskbarCreatedMsg;
        /// <summary>
        /// ボタンのアイコン 4 個（前へ／再生／一時停止／次へ）の 1 組。
        /// タスクバーの色に合わせて、白で描いた組（ダーク用）と黒で描いた組（ライト用）を持つ。
        /// </summary>
        private readonly record struct IconSet(IntPtr Prev, IntPtr Play, IntPtr Pause, IntPtr Next);

        private readonly IconSet _darkIcons, _lightIcons;

        /// <summary>タスクバーがライトか（Windows の設定「Windows モード」）。ライトなら黒いアイコンを使う。</summary>
        private bool _taskbarLight;
        private ITaskbarList3? _taskbar;
        private bool _added;
        private bool _playing;
        private bool _enabled;
        private bool _disposed;

        // 再生中かどうかで切り替えるツールチップ
        private readonly string _tipPrevious, _tipPlay, _tipPause, _tipNext;

        public TaskbarThumbButtons(IntPtr hwnd, Action<Button> onClick,
            string tipPrevious, string tipPlay, string tipPause, string tipNext)
        {
            _hwnd = hwnd;
            _onClick = onClick;
            (_tipPrevious, _tipPlay, _tipPause, _tipNext) = (tipPrevious, tipPlay, tipPause, tipNext);

            _darkIcons = LoadIconSet("");
            _lightIcons = LoadIconSet("Light");
            _taskbarLight = IsTaskbarLight();

            // タスクバーのボタンが作られた（エクスプローラーの再起動時にも来る）ことを知らせるメッセージ。
            // これを受けてからでないと ThumbBarAddButtons は失敗する
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");

            _proc = WndProc;
            SetWindowSubclass(_hwnd, _proc, SubclassId, IntPtr.Zero);
        }

        /// <summary>再生状態とキューの有無をボタンに反映する（再生中なら「一時停止」アイコン）。</summary>
        public void Update(bool playing, bool enabled)
        {
            _playing = playing;
            _enabled = enabled;
            if (_added)
                Apply(add: false);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData)
        {
            try
            {
                if (msg == _taskbarCreatedMsg)
                {
                    // エクスプローラーが作り直したときは前のボタンが消えているので、追加し直す
                    _added = false;
                    Apply(add: true);
                }
                else if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero
                         && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    // Windows のライト／ダークが切り替わった（lParam が "ImmersiveColorSet" の WM_SETTINGCHANGE が来る）。
                    // タスクバーの色が変わったときだけアイコンを差し替える
                    bool light = IsTaskbarLight();
                    if (light != _taskbarLight)
                    {
                        _taskbarLight = light;
                        if (_added)
                            Apply(add: false);
                    }
                }
                else if (msg == WM_COMMAND && HiWord(wParam) == THBN_CLICKED)
                {
                    _onClick((Button)LoWord(wParam));
                    return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                // ここで例外を外へ出すとウィンドウごと落ちるので、記録だけして続ける
                AppRuntime.AppendLifetimeLog($"thumbbar-failed {ex.GetType().Name}: {ex.Message}");
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>3 つのボタンを追加（初回）または更新する。</summary>
        private void Apply(bool add)
        {
            _taskbar ??= (ITaskbarList3)new TaskbarListClass();
            if (add && !_added)
                _taskbar.HrInit();

            uint flags = _enabled ? THBF_ENABLED : THBF_DISABLED;
            // ライトのタスクバーは背景が白っぽいので黒いアイコン、ダークは白いアイコン
            var icons = _taskbarLight ? _lightIcons : _darkIcons;
            var buttons = new[]
            {
                MakeButton(Button.Previous, icons.Prev, _tipPrevious, flags),
                MakeButton(Button.PlayPause, _playing ? icons.Pause : icons.Play, _playing ? _tipPause : _tipPlay, flags),
                MakeButton(Button.Next, icons.Next, _tipNext, flags)
            };

            int hr = add && !_added
                ? _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons)
                : _taskbar.ThumbBarUpdateButtons(_hwnd, (uint)buttons.Length, buttons);
            if (hr >= 0)
                _added = true;
        }

        private static THUMBBUTTON MakeButton(Button id, IntPtr icon, string tip, uint flags) => new()
        {
            dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
            iId = (uint)id,
            hIcon = icon,
            szTip = tip,
            dwFlags = flags
        };

        /// <summary>Assets のアイコン 1 組を読む。suffix は "" （白・ダーク用）か "Light"（黒・ライト用）。</summary>
        private static IconSet LoadIconSet(string suffix)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets");
            return new IconSet(
                LoadIcon(Path.Combine(dir, $"ThumbPrevious{suffix}.ico")),
                LoadIcon(Path.Combine(dir, $"ThumbPlay{suffix}.ico")),
                LoadIcon(Path.Combine(dir, $"ThumbPause{suffix}.ico")),
                LoadIcon(Path.Combine(dir, $"ThumbNext{suffix}.ico")));
        }

        /// <summary>
        /// タスクバーがライトか。設定 → 個人用設定 → 色 の「Windows モード」の値（SystemUsesLightTheme: 1 = ライト）。
        /// アプリのテーマ（AppsUseLightTheme / アプリ内の設定）とは別物で、タスクバーやプレビューの背景はこちらに従う。
        /// 読めないとき（古い Windows など）はダーク扱い（従来どおり白いアイコン）。
        /// </summary>
        private static bool IsTaskbarLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr LoadIcon(string path) =>
            File.Exists(path) ? LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE) : IntPtr.Zero;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
            foreach (var set in new[] { _darkIcons, _lightIcons })
            foreach (var icon in new[] { set.Prev, set.Play, set.Pause, set.Next })
            {
                if (icon != IntPtr.Zero)
                    DestroyIcon(icon);
            }
            if (_taskbar != null)
                Marshal.ReleaseComObject(_taskbar);
        }

        private static int LoWord(IntPtr v) => (int)((long)v & 0xFFFF);
        private static int HiWord(IntPtr v) => (int)(((long)v >> 16) & 0xFFFF);

        // ───────── Win32 / COM の宣言 ─────────

        private static readonly UIntPtr SubclassId = (UIntPtr)0x4D50; // このサブクラスを見分ける任意の番号
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const int THBN_CLICKED = 0x1800;
        private const uint THB_ICON = 0x2, THB_TOOLTIP = 0x4, THB_FLAGS = 0x8;
        private const uint THBF_ENABLED = 0x0, THBF_DISABLED = 0x1;
        private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr refData);

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr refData);

        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct THUMBBUTTON
        {
            public uint dwMask;
            public uint iId;
            public uint iBitmap;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szTip;
            public uint dwFlags;
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarListClass { }

        /// <summary>
        /// ITaskbarList3。COM の仕様上、親インターフェース（ITaskbarList / ITaskbarList2）のメソッドも
        /// 順番どおりに並べる必要がある（呼び出しは「何番目のメソッドか」で決まるため）。使わないものも宣言だけする。
        /// </summary>
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            // ITaskbarList
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
            // ITaskbarList2
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            // ITaskbarList3
            [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
            [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
            [PreserveSig] int UnregisterTab(IntPtr hwndTab);
            [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
            [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
            [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        }
    }
}
