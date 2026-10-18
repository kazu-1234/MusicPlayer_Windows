using System;
using System.Runtime.InteropServices;
using MusicPlayer.Services;

namespace MusicPlayer.Native
{
    /// <summary>
    /// Windows の日本語の文字列比較（CompareStringEx の ja-JP）。WMP やエクスプローラーと同じ並びになる。
    /// <para>
    /// .NET の CompareInfo は内部で ICU という別の仕組みを使うため、ハイフンの扱いや漢字の順番が Windows と違う。
    /// Windows の ja-JP は「XJIS」順で、漢字は JIS コード順（第 1 水準は代表的な音読みの順）、
    /// ひらがなとカタカナは区別せず、「ー」は前の母音として読み、ハイフンとアポストロフィは無視する。
    /// WMP で並べたプレイリスト（666 曲）と 1 曲も違わずに一致することを確かめてある。
    /// </para>
    /// </summary>
    public static class NativeSort
    {
        /// <summary>大文字小文字を区別しない（NORM_IGNORECASE）。</summary>
        private const uint NormIgnoreCase = 0x1;

        /// <summary>この API が使えない環境だったら true にして、以後は .NET の比較を使う。</summary>
        private static bool _unavailable;

        /// <summary>
        /// 2 段階で比べる:
        /// 1) 大文字小文字を区別せずに比べる（Universe → uni-verse のように、ハイフンの有無などで決まる）
        /// 2) それでも同じなら区別して比べる（HeLLo → HELLO のように、小文字が先）
        /// </summary>
        public static int Compare(string a, string b)
        {
            if (!_unavailable)
            {
                try
                {
                    int r = CompareStringEx("ja-JP", NormIgnoreCase, a, a.Length, b, b.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (r != 0)
                    {
                        // 戻り値は 1 = a が小さい、2 = 等しい、3 = a が大きい。-1/0/1 に直す
                        if (r != 2)
                            return r - 2;
                        r = CompareStringEx("ja-JP", 0, a, a.Length, b, b.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        if (r != 0)
                            return r - 2;
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _unavailable = true;
                }
            }

            // 予備: .NET の日本語比較（並びは少し違うが、並べ替え自体は動く）
            return ReadingService.JapaneseCompare.Compare(a, b, ReadingService.JapaneseOptions);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int CompareStringEx(string locale, uint flags, string s1, int c1, string s2, int c2,
            IntPtr versionInfo, IntPtr reserved, IntPtr param);
    }
}
