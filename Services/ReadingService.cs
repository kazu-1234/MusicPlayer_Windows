using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 名前順の並べ替えに使う「読み」を作る。
    /// 漢字はそのままだと文字コード順（読みとは無関係）に並ぶため、Windows 標準の日本語 IME 辞書
    /// （JapanesePhoneticAnalyzer）で単語ごとにかなへ直す。音読み・訓読みのどちらになるかは、辞書が単語単位で判断する
    /// （例: 「高橋優」→ たかはしゆう、「人間開花」→ にんげんかいか）。
    /// </summary>
    public static class ReadingService
    {
        /// <summary>同じ文字列を何度も辞書にかけないための記録（アーティスト名などは何百曲で共通）。</summary>
        private static readonly ConcurrentDictionary<string, string> Memo = new();

        /// <summary>JapanesePhoneticAnalyzer に一度に渡せる最大文字数（API の制限）。</summary>
        private const int AnalyzerMaxLength = 100;

        /// <summary>
        /// 表示名と、タグのソート用の読み（TITLESORT など。無ければ空）から、並べ替え用の読みを返す。
        /// 返す文字列はひらがな・半角・小文字にそろえてある（比較しやすくするため）。
        /// </summary>
        public static string For(string text, string tagSort = "")
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // タグの読みは「かなで書かれているとき」だけ使う。ローマ字（例: Arashi）だと、
            // かなの読みと混ぜて比べたときに順番がおかしくなるため
            if (!string.IsNullOrWhiteSpace(tagSort) && IsKanaOrLatinOnly(tagSort) && tagSort.Any(IsKana))
                return Normalize(tagSort);

            // 漢字が無ければ辞書は不要（かな・英数字はそのまま比べられる）
            if (!text.Any(IsKanji))
                return Normalize(text);

            return Memo.GetOrAdd(text, t => Normalize(Analyze(t)));
        }

        // ───────── 辞書を呼ぶ専用スレッド ─────────
        // JapanesePhoneticAnalyzer は STA（シングルスレッド アパートメント）のスレッドからしか呼べない
        // （スレッドプールなどの MTA から呼ぶと「ClassFactory は要求されたクラスを提供できません」で失敗する）。
        // ライブラリのスキャンはスレッドプールで並列に動くので、STA のスレッドを 1 本だけ作り、変換はすべてそこで行う。

        private static readonly BlockingCollection<(string Text, TaskCompletionSource<string> Done)> Queue = new();
        private static readonly Lazy<Thread> StaThread = new(() =>
        {
            var thread = new Thread(() =>
            {
                foreach (var (text, done) in Queue.GetConsumingEnumerable())
                    done.SetResult(AnalyzeCore(text));
            })
            {
                IsBackground = true, // アプリ終了を妨げない
                Name = "ReadingAnalyzer"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread;
        });

        /// <summary>辞書で漢字をかなにする（STA スレッドでなければ専用スレッドに頼んで結果を待つ）。</summary>
        private static string Analyze(string text)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return AnalyzeCore(text);

            _ = StaThread.Value;
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add((text, done));
            return done.Task.GetAwaiter().GetResult();
        }

        /// <summary>辞書で漢字をかなにする本体。失敗したら元の文字列のまま返す（並びが少しずれるだけで済む）。</summary>
        private static string AnalyzeCore(string text)
        {
            try
            {
                // カタカナはひらがなにしてから辞書にかける。曲名ではカタカナが送り仮名の代わりに使われることがあり
                // （例: 忘レナ唄）、そのままだと「忘」だけで読まれて ぼう になる。忘れな唄 にすると わすれな と読める。
                // 参照の 666 曲で試し、ほかの曲の読みが変わらないことを確かめてある
                text = KatakanaToHiragana(text);
                var sb = new StringBuilder();
                for (int start = 0; start < text.Length; start += AnalyzerMaxLength)
                {
                    string part = text.Substring(start, Math.Min(AnalyzerMaxLength, text.Length - start));
                    // monoRuby = false: 熟語は熟語のまま読む（1 文字ずつだと「今日」が こんにち になる等）
                    foreach (var word in JapanesePhoneticAnalyzer.GetWords(part, false))
                    {
                        // 読みに置き換えるのは漢字を含む単語だけ。英字（例: Mrs.）まで「みせす」のような
                        // カナ読みにすると、英字の並びの中で順番がおかしくなるため、それ以外は表示どおり残す
                        bool hasKanji = word.DisplayText.Any(IsKanji);
                        sb.Append(hasKanji && !string.IsNullOrEmpty(word.YomiText) ? word.YomiText : word.DisplayText);
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                AppRuntime.AppendLifetimeLog($"reading-failed {ex.GetType().Name}: {ex.Message}");
                return text;
            }
        }

        /// <summary>カタカナ（ァ〜ヶ）を同じ音のひらがなにする（長音「ー」などはそのまま）。</summary>
        private static string KatakanaToHiragana(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
            return sb.ToString();
        }

        /// <summary>
        /// 比較用にそろえる: 全角英数→半角（NFKC 正規化）、カタカナ→ひらがな、英字→小文字。
        /// </summary>
        public static string Normalize(string s)
        {
            string n = s.Trim().Normalize(NormalizationForm.FormKC);
            var sb = new StringBuilder(n.Length);
            foreach (char c in n)
            {
                // カタカナ（ァ U+30A1 〜 ヶ U+30F6）は、同じ音のひらがなとちょうど 0x60 ずれている
                sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ───────── 文字の種類 ─────────

        public static bool IsKanji(char c) =>
            c is >= '一' and <= '鿿'   // CJK 統合漢字
              or >= '㐀' and <= '䶿'   // CJK 統合漢字拡張 A
              or >= '豈' and <= '﫿'   // CJK 互換漢字
              or '々' or '〆';

        public static bool IsKana(char c) =>
            c is >= 'ぁ' and <= 'ゟ'   // ひらがな
              or >= '゠' and <= 'ヿ'   // カタカナ（長音「ー」を含む）
              or >= 'ｦ' and <= 'ﾟ';  // 半角カタカナ

        public static bool IsLatin(char c)
        {
            char h = c is >= 'Ａ' and <= 'ｚ' ? (char)(c - 0xFEE0) : c; // 全角英字は半角として扱う
            return h is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        }

        private static bool IsKanaOrLatinOnly(string s) => !s.Any(IsKanji);

        /// <summary>ja-JP の比較（かなの種類・全角半角・大文字小文字を区別しない）。</summary>
        public static readonly CompareInfo JapaneseCompare = new CultureInfo("ja-JP").CompareInfo;

        public const CompareOptions JapaneseOptions =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
    }
}
