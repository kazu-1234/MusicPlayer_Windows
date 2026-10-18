using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MusicPlayer.Services
{
    /// <summary>
    /// 共演表記（「AI Feat. カンナ」「米津玄師 + 野田洋次郎」「Creepy Nuts x 菅田将暉」など）から、
    /// アーティスト一覧でまとめる「主のアーティスト」を決める。
    /// <para>
    /// 主のアーティストが前に来るとは限らない（y × x の形もある）ので、表記の 1 番目を機械的に選ばず、
    /// 「そのアルバムの中で一番よく出てくる名前」→「ライブラリで単独の曲がある名前」→「前の方」の順で選ぶ。
    /// </para>
    /// </summary>
    public static class ArtistNames
    {
        /// <summary>
        /// 区切りの正規表現。英単語の区切り（feat. / with など）は前後が空白のときだけ区切りにする
        /// （「Within」「Mr.X」のように名前の一部になっているものを切らないため）。記号の区切りはどこでも切る。
        /// </summary>
        private static readonly Regex Separator = BuildSeparator();

        /// <summary>ゲスト参加の言葉（feat. / with など）。この前の部分が主のアーティスト。</summary>
        private static readonly Regex GuestMarker = new(
            $@"\s+(?:{string.Join("|", AppConfig.ArtistGuestWords.Select(Regex.Escape))})\s+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static Regex BuildSeparator()
        {
            string words = string.Join("|", AppConfig.ArtistSeparatorWords.Select(Regex.Escape));
            string symbols = string.Join("|", AppConfig.ArtistSeparatorSymbols.Select(Regex.Escape));
            return new Regex($@"\s+(?:{words})\s+|\s*(?:{symbols})\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        /// <summary>名前の前後に付いていることがある括弧や空白（「(feat. X)」の括弧の残りなど）。</summary>
        private static readonly char[] TrimChars = { ' ', '　', '(', ')', '（', '）', '[', ']', '【', '】' };

        /// <summary>共演表記を名前ごとに分ける。区切りが無ければ 1 名だけの一覧になる。</summary>
        public static List<string> Split(string artist)
        {
            var names = Separator.Split(artist)
                .Select(n => n.Trim(TrimChars))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return names.Count > 0 ? names : new List<string> { artist };
        }

        /// <summary>
        /// ライブラリ全体の「単独のアーティストとしての曲数」を覚えておき、アルバムごとに主アーティストを決める。
        /// </summary>
        public sealed class Resolver
        {
            /// <summary>名前 → その名前だけが付いている（共演でない）曲の数。</summary>
            private readonly Dictionary<string, int> _soloCount = new(StringComparer.OrdinalIgnoreCase);

            /// <param name="allArtists">ライブラリの全曲のアーティスト表記。</param>
            public Resolver(IEnumerable<string> allArtists)
            {
                foreach (string artist in allArtists)
                {
                    var names = Split(artist);
                    if (names.Count == 1)
                        _soloCount[names[0]] = _soloCount.GetValueOrDefault(names[0]) + 1;
                }
            }

            /// <summary>
            /// 1 枚のアルバムの曲のアーティスト表記を受け取り、「表記 → 主アーティスト」を返す関数を作る。
            /// </summary>
            public Func<string, string> ForAlbum(IReadOnlyCollection<string> albumArtists)
            {
                // アルバム内で、その名前を含む曲の数（feat. の曲も数える）
                var albumCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                // アルバム内で、まったく同じ表記の曲の数
                var exactCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string artist in albumArtists)
                {
                    exactCount[artist] = exactCount.GetValueOrDefault(artist) + 1;
                    foreach (string name in Split(artist))
                        albumCount[name] = albumCount.GetValueOrDefault(name) + 1;
                }

                return artist =>
                {
                    var names = Split(artist);
                    if (names.Count == 1)
                        return artist; // 共演ではない

                    // アルバム内の出現数 → 単独での曲数 → 表記の前の方、の順で 1 名を選ぶ
                    string best = names
                        .Select((name, index) => (name, index))
                        .OrderByDescending(x => albumCount.GetValueOrDefault(x.name))
                        .ThenByDescending(x => _soloCount.GetValueOrDefault(x.name))
                        .ThenBy(x => x.index)
                        .First().name;

                    // 単独の曲が 1 曲も無く、アルバム内でもこの表記の曲にしか出てこない名前なら、まとめる根拠が無い。
                    // 2 人組のユニット名や、全曲が同じ共演名義のアルバムを勝手にばらさないよう、表記のままにする
                    bool noEvidence = _soloCount.GetValueOrDefault(best) == 0
                                      && albumCount.GetValueOrDefault(best) <= exactCount.GetValueOrDefault(artist);
                    if (!noEvidence)
                        return best;

                    // 手がかりが無くても「A feat. B」「A with B」なら A が主（ゲスト参加の表記）。
                    // A が「X & Y」のような対等な名義なら、それ自体を 1 つのアーティストとして扱う
                    var guest = GuestMarker.Match(artist);
                    if (guest.Success && guest.Index > 0)
                        return artist[..guest.Index].Trim(TrimChars);
                    return artist;
                };
            }
        }
    }
}
