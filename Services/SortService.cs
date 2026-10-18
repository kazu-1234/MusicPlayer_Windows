using System;
using System.Collections.Generic;
using System.Linq;
using MusicPlayer.Models;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 曲・アルバム・アーティスト・プレイリスト項目の並べ替えをまとめた場所。
    /// 名前の比べ方は設定（NameSortMode）に従う。同じ値どうしは名前順を 2 番目の基準にする。
    /// </summary>
    public static class SortService
    {
        /// <summary>
        /// 今の設定の名前の並べ方（設定画面から変える）。新しく並べ替えるときにこの値を SortSpec.NameMode に記録する。
        /// 並べ替えそのものは SortSpec.NameMode を使うので、設定を切り替えただけでは今の並びは変わらない。
        /// </summary>
        public static NameSortMode Mode { get; set; } = NameSortMode.Separate;

        /// <summary>その並べ替えで使う名前の並べ方（記録が無ければ今の設定）。</summary>
        private static NameSortMode ModeOf(SortSpec spec) => spec.NameMode ?? Mode;

        // ───────── 名前の比較 ─────────

        /// <summary>
        /// 表示名と読みで 2 つの名前を比べる。
        /// - 分ける: WMP と同じ並び（英字 → かな → 漢字（JIS コード順）→ 数字始まり）。読みは使わない。
        /// - 混ぜる: 漢字で始まる名前だけ読みに置き換え、あとは「分ける」と同じ比べ方。
        ///   英字・記号・数字・かなの並びは両方で同じで、漢字はかなの中の読みの位置に入る。
        /// </summary>
        public static int CompareNames(string displayA, string readingA, string displayB, string readingB, NameSortMode mode)
        {
            if (mode == NameSortMode.Separate)
                return CompareLikeWmp(displayA, displayB);

            // 混ぜる: 漢字で始まる名前だけ読み（ひらがな）に置き換え、あとは「分ける」と同じ比べ方にする。
            // これで英字・記号・数字・かなの並びは両方の設定で同じになり、漢字はかなの中の読みの位置に入る
            int mixed = CompareLikeWmp(MixedKey(displayA, readingA), MixedKey(displayB, readingB));
            return mixed != 0 ? mixed : CompareLikeWmp(displayA, displayB);
        }

        /// <summary>混ぜるモードの比較用の文字列。先頭の記号を除いた最初の文字が漢字なら読みを使う。</summary>
        private static string MixedKey(string display, string reading)
        {
            string key = SortKeyText(display);
            return key.Length > 0 && ReadingService.IsKanji(key[0]) && reading.Length > 0 ? reading : display;
        }

        /// <summary>
        /// 「分ける」設定の名前の比較。WMP で並べたプレイリストと同じ並びになる:
        /// 英字 → ギリシャ文字など → かな → 漢字（JIS コード順）→ 数字始まり の順。
        /// 1) 先頭の記号（「 [ ( など）は飛ばして比べる（「伝言歌」は 伝 の位置に入る）
        /// 2) 数字で始まる名前は一番最後にまとめる（WMP と同じ）
        /// 3) それ以外は Windows の日本語比較（NativeSort）に任せる
        /// </summary>
        private static int CompareLikeWmp(string a, string b)
        {
            string ka = SortKeyText(a), kb = SortKeyText(b);
            int c = StartsWithDigit(ka).CompareTo(StartsWithDigit(kb)); // false(0) が先、true(1) が後
            return c != 0 ? c : Native.NativeSort.Compare(ka, kb);
        }

        /// <summary>先頭の記号・空白を取り除いた、並べ替え用の文字列。全部記号なら元のまま。</summary>
        public static string SortKeyText(string s)
        {
            int i = 0;
            while (i < s.Length && !char.IsLetterOrDigit(s[i]))
                i++;
            return i < s.Length ? s[i..] : s;
        }

        private static bool StartsWithDigit(string s) => s.Length > 0 && char.IsDigit(s[0]);

        private static int Dir(int c, bool descending) => descending ? -c : c;

        // ───────── 頭文字ごとの見出し ─────────

        /// <summary>
        /// 50 音の行の区切り。ひらがなは Unicode で「ぁあぃい…」と行の順に並んでいるので、
        /// 「次の行の最初の文字より前」ならその行になる（例: 「げ」は「さ」より前なので か行。濁音もそのまま正しく入る）。
        /// 各組は（この文字より前なら, 見出し）。小さい「ゃ」「ゎ」は や行・わ行の先頭に来るので区切りに使う。
        /// </summary>
        private static readonly (char Before, string Head)[] KanaRows =
        {
            ('か', "あ"), ('さ', "か"), ('た', "さ"), ('な', "た"), ('は', "な"),
            ('ま', "は"), ('ゃ', "ま"), ('ら', "や"), ('ゎ', "ら"), ('ゔ', "わ")
        };

        /// <summary>
        /// 名前の見出し: 記号「&amp;」・数字「#」・英字 A〜Z・かなは 50 音の行（あ・か…）。
        /// 漢字は「分ける」設定では「漢字」、「混ぜる」設定では読みの行に入れる。
        /// </summary>
        public static string NameHeaderOf(string display, string reading, NameSortMode mode)
        {
            // 並べ替えと同じく先頭の記号は飛ばして判定する（[Alexandros] は「A」に入る）。全部記号の名前だけが「&」
            char first = FirstVisible(SortKeyText(display));
            if (first == '\0')
                return "&";
            if (ReadingService.IsKanji(first) && mode == NameSortMode.Separate)
                return Strings.Get("Group_Kanji");
            if (ReadingService.IsKanji(first) || ReadingService.IsKana(first))
            {
                // 読み（ひらがなにそろえ済み）の先頭から行を決める。読みが無ければ表示名をそろえて使う
                char k = FirstVisible(reading.Length > 0 ? reading : ReadingService.Normalize(display));
                return KanaRowOf(k) ?? Strings.Get("Group_Kanji");
            }

            // 全角英数は半角にしてから判定する（ＡＢＣ も A〜Z に入れる）
            char h = ReadingService.Normalize(first.ToString())[0];
            if (h is >= '0' and <= '9')
                return "#";
            if (h is >= 'a' and <= 'z')
                return char.ToUpperInvariant(h).ToString();
            // ギリシャ文字など英字以外の文字は、その文字（大文字）を見出しにする（π → Π）
            if (char.IsLetter(h))
                return char.ToUpperInvariant(h).ToString();
            return "&";
        }

        /// <summary>ひらがな 1 文字が属する行の見出し（あ・か…）。ひらがなでなければ null。</summary>
        private static string? KanaRowOf(char c)
        {
            if (c is < 'ぁ' or > 'ゖ')
                return null; // ひらがなの範囲（ぁ〜ゖ）の外
            if (c == 'ゔ')
                return "あ"; // ゔ（う の濁音）は あ行
            if (c is 'ゕ' or 'ゖ')
                return "か"; // 小さい か・け
            foreach (var (before, head) in KanaRows)
            {
                if (c < before)
                    return head;
            }
            return "わ";
        }

        private static char FirstVisible(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsWhiteSpace(c))
                    return c;
            }
            return '\0';
        }

        /// <summary>年の見出し（不明は「—」）。</summary>
        public static string YearHeaderOf(uint year) => year > 0 ? year.ToString() : "—";

        /// <summary>追加日の見出し（「2026年10月」のような年月）。</summary>
        public static string AddedHeaderOf(long ticks) =>
            ticks <= 0 ? "—" : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString(Strings.Get("Group_AddedFormat"));

        /// <summary>アルバムの見出し（並べ替えの基準に合わせる）。</summary>
        public static string HeaderOf(Album a, SortSpec spec) => spec.Key switch
        {
            SortKey.Artist => NameHeaderOf(a.Artist, a.ArtistReading, ModeOf(spec)),
            SortKey.Year => YearHeaderOf(a.Year),
            SortKey.Added => AddedHeaderOf(a.AddedTicks),
            _ => NameHeaderOf(a.Title, a.TitleReading, ModeOf(spec))
        };

        /// <summary>アーティストの見出し。</summary>
        public static string HeaderOf(Artist a, SortSpec spec) =>
            spec.Key == SortKey.Added ? AddedHeaderOf(a.AddedTicks) : NameHeaderOf(a.Name, a.Reading, ModeOf(spec));

        /// <summary>
        /// 並べ替え済みの一覧を、先頭から順に「同じ見出しが続く範囲」でまとめる。
        /// 並べ替えの結果をそのまま使うので、降順なら見出しの並びも逆になる。
        /// </summary>
        public static List<ItemGroup<T>> GroupConsecutive<T>(IEnumerable<T> sorted, Func<T, string> headerOf)
        {
            var groups = new List<ItemGroup<T>>();
            foreach (var item in sorted)
            {
                string header = headerOf(item);
                if (groups.Count == 0 || groups[^1].Key != header)
                    groups.Add(new ItemGroup<T>(header));
                groups[^1].Add(item);
            }
            return groups;
        }

        // ───────── 曲 ─────────

        public static List<Track> Sort(IEnumerable<Track> tracks, SortSpec spec) =>
            tracks.OrderBy(t => t, Comparer<Track>.Create((a, b) => CompareTracks(a, b, spec))).ToList();

        public static int CompareTracks(Track a, Track b, SortSpec spec)
        {
            var mode = ModeOf(spec);
            int c = spec.Key switch
            {
                SortKey.Artist => CompareNames(a.DisplayArtist, a.ArtistReading, b.DisplayArtist, b.ArtistReading, mode),
                SortKey.Album => CompareNames(a.DisplayAlbum, a.AlbumReading, b.DisplayAlbum, b.AlbumReading, mode),
                SortKey.Year => a.Year.CompareTo(b.Year),
                SortKey.Added => a.AddedTicks.CompareTo(b.AddedTicks),
                SortKey.Format => string.CompareOrdinal(a.Format, b.Format),
                SortKey.Duration => a.DurationSeconds.CompareTo(b.DurationSeconds),
                SortKey.TrackNumber => CompareTrackNumbers(a, b),
                _ => 0
            };
            if (c != 0)
                return Dir(c, spec.Descending);

            // 名前順、または同じ値どうしは曲名で（名前順のときだけ向きを反映する）
            c = CompareNames(a.DisplayTitle, a.TitleReading, b.DisplayTitle, b.TitleReading, mode);
            return spec.Key == SortKey.Name ? Dir(c, spec.Descending) : c;
        }

        /// <summary>ディスク番号 → トラック番号の順。番号の無い曲は後ろ（アルバムの並びと同じ考え方）。</summary>
        private static int CompareTrackNumbers(Track a, Track b)
        {
            int c = a.DiscNumber.CompareTo(b.DiscNumber);
            if (c != 0)
                return c;
            uint na = a.TrackNumber == 0 ? uint.MaxValue : a.TrackNumber;
            uint nb = b.TrackNumber == 0 ? uint.MaxValue : b.TrackNumber;
            return na.CompareTo(nb);
        }

        // ───────── アルバム ─────────

        public static List<Album> Sort(IEnumerable<Album> albums, SortSpec spec) =>
            albums.OrderBy(a => a, Comparer<Album>.Create((a, b) =>
            {
                int c = spec.Key switch
                {
                    SortKey.Artist => CompareNames(a.Artist, a.ArtistReading, b.Artist, b.ArtistReading, ModeOf(spec)),
                    SortKey.Year => a.Year.CompareTo(b.Year),
                    SortKey.Added => a.AddedTicks.CompareTo(b.AddedTicks),
                    _ => 0
                };
                if (c != 0)
                    return Dir(c, spec.Descending);

                c = CompareNames(a.Title, a.TitleReading, b.Title, b.TitleReading, ModeOf(spec));
                if (c == 0)
                    c = string.CompareOrdinal(a.Format, b.Format); // 同名の FLAC 版と MP3 版
                return spec.Key == SortKey.Name ? Dir(c, spec.Descending) : c;
            })).ToList();

        // ───────── アーティスト ─────────

        public static List<Artist> Sort(IEnumerable<Artist> artists, SortSpec spec) =>
            artists.OrderBy(a => a, Comparer<Artist>.Create((a, b) =>
            {
                int c = spec.Key == SortKey.Added ? a.AddedTicks.CompareTo(b.AddedTicks) : 0;
                if (c != 0)
                    return Dir(c, spec.Descending);

                c = CompareNames(a.Name, a.Reading, b.Name, b.Reading, ModeOf(spec));
                return spec.Key == SortKey.Added ? c : Dir(c, spec.Descending);
            })).ToList();
    }
}
