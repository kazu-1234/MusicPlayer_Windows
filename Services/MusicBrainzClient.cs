using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MusicPlayer.Services
{
    /// <summary>CD 1 枚分の曲情報（MusicBrainz の検索結果 1 件）。</summary>
    public sealed class CdReleaseInfo
    {
        public string ReleaseId { get; init; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Year { get; set; } = "";
        public int DiscNumber { get; set; } = 1;
        public int DiscCount { get; set; } = 1;
        public string Country { get; init; } = "";

        /// <summary>ジャンル（MusicBrainz の genres のうち、投票数が最も多いもの）。無ければ空。</summary>
        public string Genre { get; set; } = "";

        /// <summary>曲一覧。CD 取り込みでは対象の 1 枚分、タグ編集（GetReleaseAsync）では全ディスク分が入る。</summary>
        public List<CdTrackInfo> Tracks { get; } = new();

        /// <summary>候補選択の ComboBox に出す文字列。</summary>
        public string Display =>
            $"{Title} / {Artist}" + (Year.Length > 0 ? $" ({Year})" : "") + (Country.Length > 0 ? $" [{Country}]" : "")
            + (DiscCount > 1 ? $" Disc {DiscNumber}/{DiscCount}" : "");
    }

    public sealed class CdTrackInfo
    {
        public int Number { get; init; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";

        /// <summary>何枚目のディスクの曲か（1 始まり）。</summary>
        public int DiscNumber { get; init; } = 1;

        /// <summary>そのディスクの曲数。</summary>
        public int DiscTrackCount { get; init; }

        /// <summary>MusicBrainz の録音 ID（曲名検索の結果と、アルバム詳細の曲を対応付けるのに使う）。</summary>
        public string RecordingId { get; init; } = "";
    }

    /// <summary>タグ編集の「オンラインで取得」の候補 1 件。選ばれたら ReleaseId でアルバムの詳細を取り直す。</summary>
    public sealed class OnlineCandidate
    {
        public string ReleaseId { get; init; } = "";

        /// <summary>1 曲検索のときの録音 ID（アルバム内のどの曲かを特定する）。アルバム検索では空。</summary>
        public string RecordingId { get; init; } = "";

        /// <summary>候補選択の ComboBox に出す文字列。</summary>
        public string Display { get; init; } = "";
    }

    /// <summary>
    /// MusicBrainz（無料の音楽データベース）から、CD の Disc ID で曲名などを取得する。
    /// 規約: 1 秒に 1 リクエストまで・連絡先付きの User-Agent が必須。
    /// </summary>
    public static class MusicBrainzClient
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim RateGate = new(1, 1);
        private static DateTime _lastRequestUtc = DateTime.MinValue;

        /// <summary>
        /// Disc ID で検索する。完全一致が無い場合は TOC（各トラックの位置）による曖昧検索の結果も返る。
        /// </summary>
        public static async Task<List<CdReleaseInfo>> LookupAsync(string discId, string toc, int audioTrackCount, CancellationToken token)
        {
            string url = $"https://musicbrainz.org/ws/2/discid/{Uri.EscapeDataString(discId)}"
                         + $"?toc={toc}&inc=recordings+artist-credits+genres&fmt=json";
            string? json = await GetStringAsync(url, token);
            var results = new List<CdReleaseInfo>();
            if (json == null)
                return results;

            var root = JObject.Parse(json);
            foreach (var release in root["releases"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var media = release["media"]?.Children<JObject>().ToList() ?? new List<JObject>();
                if (media.Count == 0)
                    continue;

                // 複数枚組の場合、この CD に当たる 1 枚を選ぶ（Disc ID 一致 → 曲数一致 → 先頭）
                JObject medium =
                    media.FirstOrDefault(m => m["discs"]?.Any(d => (string?)d["id"] == discId) == true)
                    ?? media.FirstOrDefault(m => (int?)m["track-count"] == audioTrackCount)
                    ?? media[0];

                string albumArtist = CreditName(release["artist-credit"]);
                var info = new CdReleaseInfo
                {
                    ReleaseId = (string?)release["id"] ?? "",
                    Title = (string?)release["title"] ?? "",
                    Artist = albumArtist,
                    Year = ((string?)release["date"] ?? "").Split('-')[0],
                    Country = (string?)release["country"] ?? "",
                    DiscNumber = (int?)medium["position"] ?? 1,
                    DiscCount = media.Count,
                    Genre = TopGenre(release)
                };

                foreach (var track in medium["tracks"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    string trackArtist = CreditName(track["artist-credit"]);
                    info.Tracks.Add(new CdTrackInfo
                    {
                        Number = (int?)track["position"] ?? info.Tracks.Count + 1,
                        Title = (string?)track["title"] ?? "",
                        Artist = trackArtist.Length > 0 ? trackArtist : albumArtist
                    });
                }

                results.Add(info);
            }

            return results;
        }

        /// <summary>
        /// Cover Art Archive からジャケット画像（表紙）を取得する。無ければ null。
        /// Windows メディア プレーヤーで取り込んだ曲と同じく高解像度で埋め込むため、原寸（front）を取り、
        /// 大きすぎるときだけ AppConfig.EmbeddedCoverMaxSize に縮める。
        /// </summary>
        public static async Task<byte[]?> GetCoverAsync(string releaseId, CancellationToken token)
        {
            if (string.IsNullOrEmpty(releaseId))
                return null;

            try
            {
                // リダイレクト先（archive.org）へは HttpClient が自動で追従する
                using var response = await Http.GetAsync($"https://coverartarchive.org/release/{releaseId}/front", token);
                if (!response.IsSuccessStatusCode)
                    return null;
                byte[] data = await response.Content.ReadAsByteArrayAsync(token);
                return await ImageUtil.FitForEmbeddingAsync(data);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }

        // ───────── タグ編集用の検索 ─────────

        /// <summary>
        /// 曲名（＋アーティスト・アルバム）で録音を検索し、「その曲が入っているアルバム」ごとの候補を返す。
        /// </summary>
        public static async Task<List<OnlineCandidate>> SearchRecordingsAsync(string title, string artist, string album, CancellationToken token)
        {
            var terms = new List<string> { Field("recording", title) };
            if (artist.Length > 0) terms.Add(Field("artist", artist));
            if (album.Length > 0) terms.Add(Field("release", album));

            string? json = await GetStringAsync(
                $"https://musicbrainz.org/ws/2/recording?query={Uri.EscapeDataString(string.Join(" AND ", terms))}"
                + $"&limit={AppConfig.OnlineLookupMaxCandidates}&fmt=json", token);
            var results = new List<OnlineCandidate>();
            if (json == null)
                return results;

            var found = new List<(bool Exact, OnlineCandidate Candidate)>();
            foreach (var rec in JObject.Parse(json)["recordings"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string recTitle = (string?)rec["title"] ?? "";
                string recArtist = CreditName(rec["artist-credit"]);
                foreach (var rel in rec["releases"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    string year = ((string?)rel["date"] ?? "").Split('-')[0];
                    string country = (string?)rel["country"] ?? "";
                    found.Add((string.Equals(recTitle, title.Trim(), StringComparison.CurrentCultureIgnoreCase), new OnlineCandidate
                    {
                        ReleaseId = (string?)rel["id"] ?? "",
                        RecordingId = (string?)rec["id"] ?? "",
                        Display = $"{recTitle} / {recArtist} — {(string?)rel["title"]}"
                                  + (year.Length > 0 ? $" ({year})" : "") + (country.Length > 0 ? $" [{country}]" : "")
                    }));
                }
            }

            // 曲名が完全に一致する録音を先頭に（「DEJAVU (Music Clip)」より「DEJAVU」を先に出す）。
            // OrderBy は安定ソートなので、同じ条件の中では MusicBrainz の関連度順が保たれる
            results.AddRange(found.OrderBy(x => x.Exact ? 0 : 1).Select(x => x.Candidate).Take(AppConfig.OnlineLookupMaxCandidates));
            return results;
        }

        /// <summary>アルバム名（＋アルバムアーティスト）でアルバムを検索する（複数ファイルの一括編集用）。</summary>
        public static async Task<List<OnlineCandidate>> SearchReleasesAsync(string album, string artist, CancellationToken token)
        {
            var terms = new List<string> { Field("release", album) };
            if (artist.Length > 0) terms.Add(Field("artist", artist));

            string? json = await GetStringAsync(
                $"https://musicbrainz.org/ws/2/release?query={Uri.EscapeDataString(string.Join(" AND ", terms))}"
                + $"&limit={AppConfig.OnlineLookupMaxCandidates}&fmt=json", token);
            var results = new List<OnlineCandidate>();
            if (json == null)
                return results;

            foreach (var rel in JObject.Parse(json)["releases"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string year = ((string?)rel["date"] ?? "").Split('-')[0];
                string country = (string?)rel["country"] ?? "";
                int count = (int?)rel["track-count"] ?? 0;
                results.Add(new OnlineCandidate
                {
                    ReleaseId = (string?)rel["id"] ?? "",
                    Display = $"{(string?)rel["title"]} / {CreditName(rel["artist-credit"])}"
                              + (year.Length > 0 ? $" ({year})" : "") + (country.Length > 0 ? $" [{country}]" : "")
                              + (count > 0 ? $" · {count}" : "")
                });
            }
            return results;
        }

        /// <summary>アルバムの詳細（全ディスクの曲・ジャンル・アルバムアーティスト）を取得する。見つからなければ null。</summary>
        public static async Task<CdReleaseInfo?> GetReleaseAsync(string releaseId, CancellationToken token)
        {
            string? json = await GetStringAsync(
                $"https://musicbrainz.org/ws/2/release/{Uri.EscapeDataString(releaseId)}"
                + "?inc=recordings+artist-credits+genres+release-groups&fmt=json", token);
            if (json == null)
                return null;

            var release = JObject.Parse(json);
            var media = release["media"]?.Children<JObject>().ToList() ?? new List<JObject>();
            string albumArtist = CreditName(release["artist-credit"]);
            var info = new CdReleaseInfo
            {
                ReleaseId = releaseId,
                Title = (string?)release["title"] ?? "",
                Artist = albumArtist,
                Year = ((string?)release["date"] ?? "").Split('-')[0],
                Country = (string?)release["country"] ?? "",
                DiscCount = Math.Max(1, media.Count),
                Genre = TopGenre(release)
            };

            foreach (var medium in media)
            {
                int disc = (int?)medium["position"] ?? 1;
                int count = (int?)medium["track-count"] ?? 0;
                foreach (var track in medium["tracks"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    string trackArtist = CreditName(track["artist-credit"]);
                    info.Tracks.Add(new CdTrackInfo
                    {
                        Number = (int?)track["position"] ?? 0,
                        Title = (string?)track["title"] ?? "",
                        Artist = trackArtist.Length > 0 ? trackArtist : albumArtist,
                        DiscNumber = disc,
                        DiscTrackCount = count,
                        RecordingId = (string?)track["recording"]?["id"] ?? ""
                    });
                }
            }
            return info;
        }

        /// <summary>
        /// 検索クエリ（Lucene 構文）の 1 項目「field:"値"」を作る。
        /// 値の中の \ と " は前に \ を付け、区切り記号ではなくただの文字として扱わせる。
        /// </summary>
        private static string Field(string name, string value) =>
            name + ":\"" + value.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>
        /// genres（投票数付き）から最も票の多いものを選ぶ。アルバム自体に無ければリリースグループ（同じ作品の別版をまとめた単位）から探す。
        /// MusicBrainz は小文字（例: j-pop）なので、単語の先頭を大文字にする（J-Pop）。
        /// </summary>
        private static string TopGenre(JObject release)
        {
            JToken? genres = release["genres"] is JArray { Count: > 0 } g ? g : release["release-group"]?["genres"];
            if (genres is not JArray array || array.Count == 0)
                return "";

            string name = array.OrderByDescending(x => (int?)x["count"] ?? 0).Select(x => (string?)x["name"] ?? "").FirstOrDefault() ?? "";
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
        }

        /// <summary>レート制限を守って GET する。404（見つからない）は null。</summary>
        private static async Task<string?> GetStringAsync(string url, CancellationToken token)
        {
            await RateGate.WaitAsync(token);
            try
            {
                var wait = _lastRequestUtc.AddMilliseconds(AppConfig.MusicBrainzIntervalMs) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, token);

                var response = await Http.GetAsync(url, token);
                _lastRequestUtc = DateTime.UtcNow;
                // 503 = MusicBrainz が混雑中（レート制限）。少し待って 1 回だけやり直す
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    response.Dispose();
                    await Task.Delay(AppConfig.MusicBrainzBusyRetryDelayMs, token);
                    response = await Http.GetAsync(url, token);
                    _lastRequestUtc = DateTime.UtcNow;
                }
                using var _ = response;
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token);
            }
            finally
            {
                RateGate.Release();
            }
        }

        /// <summary>artist-credit 配列（「A feat. B」のように名前と区切り文字の組）を 1 つの文字列にする。</summary>
        private static string CreditName(JToken? credits)
        {
            if (credits is not JArray array)
                return "";
            return string.Concat(array.Select(c => ((string?)c["name"] ?? "") + ((string?)c["joinphrase"] ?? "")));
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConfig.MusicBrainzUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }
    }
}
