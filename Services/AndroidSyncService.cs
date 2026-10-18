using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MusicPlayer.Services
{
    /// <summary>
    /// Android 版 MusicPlayer への一方向同期（PC → Android）。
    ///
    /// しくみ:
    /// - Android 側が「受信待機」中に HTTP サーバー（ポート AppConfig.SyncHttpPort）を開いて待つ。PC はクライアントとして送るだけ。
    ///   PC 側で待ち受けないので、Windows ファイアウォールの許可は要らない。
    /// - 端末は UDP ブロードキャストで探す（見つからなければ IP を手入力）。
    /// - 初回は Android に表示される 6 桁の PIN でペアリングし、受け取ったトークンを以後のヘッダ X-Token に付ける。
    /// - ファイルは「PC のミュージックからの相対パス」で対応づける。Android 側の「同期先フォルダ」（/Music）からの相対パスと同じになる。
    ///
    /// AppRuntime が所有する（ウィンドウを閉じ直しても同期の状態を持ち続けるため）。
    /// </summary>
    public sealed class AndroidSyncService : IDisposable
    {
        private readonly Settings _settings;
        private readonly LibraryService _library;
        private readonly PlaylistService _playlists;

        /// <summary>
        /// 全要求で使い回す HttpClient（接続を使い回して速くするため 1 つだけ作る）。
        /// 要求ごとに長さの違うタイムアウトを付けたいので、全体の Timeout は無効にして CancellationToken で切る。
        /// </summary>
        private readonly HttpClient _http = new(new SocketsHttpHandler
        {
            // Android 側は一定時間（60 秒）やり取りの無い接続を切る。切られた接続を使い回して失敗しないよう、それより先に手放す
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(AppConfig.SyncIdleConnectionSeconds)
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>Android 側（Kotlin / Gson）は camelCase の JSON を読むので、送る JSON の名前を camelCase にそろえる。</summary>
        private static readonly JsonSerializerSettings JsonOut = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        public AndroidSyncService(Settings settings, LibraryService library, PlaylistService playlists)
        {
            _settings = settings;
            _library = library;
            _playlists = playlists;
        }

        /// <summary>同期の実行中か（画面を離れて戻ってきたときに、ボタンの状態を合わせるため）。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>この PC の ID。初めて使うときに作って保存する。</summary>
        private string ClientId
        {
            get
            {
                if (string.IsNullOrEmpty(_settings.SyncClientId))
                {
                    _settings.SyncClientId = Guid.NewGuid().ToString("N");
                    _settings.Save();
                }
                return _settings.SyncClientId;
            }
        }

        // ───────── 端末の探索 ─────────

        /// <summary>
        /// 同じネットワークで受信待機中の Android を探す。
        /// 255.255.255.255 に加え、各ネットワークアダプタのサブネット向けブロードキャスト（192.168.1.255 など）にも送る。
        /// 有線と Wi-Fi など複数のアダプタがあると、255.255.255.255 は片方にしか出ないことがあるため。
        /// </summary>
        public async Task<List<SyncDevice>> DiscoverAsync(CancellationToken token)
        {
            var found = new Dictionary<string, SyncDevice>();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            byte[] message = Encoding.UTF8.GetBytes(AppConfig.SyncDiscoveryMessage);
            var targets = GetBroadcastAddresses();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(AppConfig.SyncDiscoveryTimeoutMs);

            // 返事を受け取る処理を先に始めてから送る（送った直後に返ってくる返事を取りこぼさないため）
            var receiveTask = Task.Run(async () =>
            {
                while (!timeout.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await udp.ReceiveAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        continue; // 届かなかった送信先からの ICMP エラーなどは無視して待ち続ける
                    }

                    var device = ParseDiscoveryReply(result.Buffer, result.RemoteEndPoint.Address);
                    if (device != null)
                    {
                        lock (found)
                            found[device.DeviceId] = device;
                    }
                }
            });

            for (int i = 0; i < AppConfig.SyncDiscoveryRepeat && !timeout.IsCancellationRequested; i++)
            {
                foreach (var address in targets)
                {
                    try
                    {
                        await udp.SendAsync(message, message.Length, new IPEndPoint(address, AppConfig.SyncDiscoveryPort));
                    }
                    catch (SocketException)
                    {
                        // 使えないアダプタ宛ての送信失敗は無視する
                    }
                }
                try
                {
                    await Task.Delay(AppConfig.SyncDiscoveryTimeoutMs / (AppConfig.SyncDiscoveryRepeat + 1), timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await receiveTask;
            token.ThrowIfCancellationRequested();
            lock (found)
                return found.Values.ToList();
        }

        private static List<IPAddress> GetBroadcastAddresses()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                            continue;
                        // ブロードキャストアドレス = IP の、マスクで 0 の部分（ホスト部）をすべて 1 にしたもの
                        byte[] ip = unicast.Address.GetAddressBytes();
                        byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                            broadcast[i] = (byte)(ip[i] | ~mask[i]);
                        var address = new IPAddress(broadcast);
                        if (!list.Contains(address))
                            list.Add(address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // アダプタ一覧が取れなくても 255.255.255.255 だけで探す
            }
            return list;
        }

        /// <summary>Android の返事（JSON: app / deviceId / name / port）を読む。MusicPlayer 以外からの返事は捨てる。</summary>
        private static SyncDevice? ParseDiscoveryReply(byte[] buffer, IPAddress from)
        {
            try
            {
                var json = JObject.Parse(Encoding.UTF8.GetString(buffer));
                if ((string?)json["app"] != "MusicPlayer")
                    return null;
                string? id = (string?)json["deviceId"];
                if (string.IsNullOrEmpty(id))
                    return null;
                return new SyncDevice
                {
                    DeviceId = id,
                    Name = (string?)json["name"] ?? "",
                    Host = from.ToString(),
                    Port = (int?)json["port"] ?? AppConfig.SyncHttpPort
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ───────── 端末情報・ペアリング ─────────

        /// <summary>IP を手入力したときに、その端末の ID と名前を問い合わせる（認証不要の /info）。</summary>
        public async Task<SyncDevice> GetInfoAsync(string host, int port, CancellationToken token)
        {
            var json = await SendJsonAsync(HttpMethod.Get, host, port, "/info", null, null, token);
            return new SyncDevice
            {
                DeviceId = (string?)json["deviceId"] ?? "",
                Name = (string?)json["name"] ?? "",
                Host = host,
                Port = port
            };
        }

        /// <summary>
        /// Android に表示されている PIN でペアリングし、トークンを保存する。
        /// 成功すると settings の端末一覧に追加（または更新）した端末を返す。
        /// </summary>
        public async Task<SyncDevice> PairAsync(SyncDevice device, string pin, CancellationToken token)
        {
            var body = new { pin = pin.Trim(), clientId = ClientId, clientName = Environment.MachineName };
            var json = await SendJsonAsync(HttpMethod.Post, device.Host, device.Port, "/pair", null, body, token);
            string? newToken = (string?)json["token"];
            if (string.IsNullOrEmpty(newToken))
                throw new InvalidOperationException(Strings.Get("Sync_PairFailed"));

            device.Token = newToken;
            RememberDevice(device);
            return device;
        }

        /// <summary>端末を settings に保存する（同じ ID があれば IP・名前・トークンを新しくする）。</summary>
        public void RememberDevice(SyncDevice device)
        {
            var existing = _settings.SyncDevices.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            if (existing == null)
            {
                _settings.SyncDevices.Add(device);
            }
            else
            {
                existing.Name = device.Name;
                existing.Host = device.Host;
                existing.Port = device.Port;
                if (device.IsPaired)
                    existing.Token = device.Token;
            }
            _settings.LastSyncDeviceId = device.DeviceId;
            _settings.Save();
        }

        public void ForgetDevice(string deviceId)
        {
            _settings.SyncDevices.RemoveAll(d => d.DeviceId == deviceId);
            if (_settings.LastSyncDeviceId == deviceId)
                _settings.LastSyncDeviceId = "";
            _settings.Save();
        }

        // ───────── PC 側の候補 ─────────

        /// <summary>
        /// ライブラリの全曲を同期候補の形（相対パス・拡張子・フォルダ単位・サイズ）にする。
        /// ライブラリの一覧を元にするので、ライブラリに入っていない曲は同期しない（プレイリストの再生可否と同じ考え方）。
        /// </summary>
        public Task<List<SyncLocalFile>> BuildCatalogAsync() => Task.Run(() =>
        {
            // 相対パスの基準。Android の同期先フォルダは PC の「ミュージック\Music」（FLAC・MP3 フォルダが並ぶ所）と同じ形にするので、
            // その下の曲は基準から数える（PC の Music\Music\FLAC\A\B.flac → Android の 同期先/FLAC/A/B.flac）。
            // 基準の外にあるライブラリフォルダの曲は、フォルダ名ごと同期先の直下に置く（最近追加した曲\x.mp3 → 同期先/最近追加した曲/x.mp3）。
            // そのため照合するフォルダ（Match）と、相対パスを数え始める起点（Start）を組で持つ。
            string baseFolder = WithSlash(AppConfig.SyncBaseFolder);
            var roots = _settings.LibraryFolders
                .Where(Directory.Exists)
                .Select(WithSlash)
                // 基準の下のライブラリフォルダ（Music\Music\FLAC など）は基準でまとめて扱う
                .Where(r => !r.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase))
                .Select(r => (Match: r, Start: StartOf(r)))
                .Append((Match: baseFolder, Start: baseFolder))
                // 長いフォルダから当てる（親子関係のフォルダが両方あっても、近い方を使うため。基準は祖先のフォルダより先に当たる）
                .OrderByDescending(r => r.Match.Length)
                .ToList();

            // 起点 = ライブラリフォルダの親（フォルダ名が相対パスの先頭に残る）。
            // ただし基準の祖先（「ミュージック」自体など）は自身から数える（親の「kazuh」などが入らないように）。ドライブ直下も自身。
            string StartOf(string folder)
            {
                if (baseFolder.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    return folder;
                string? parent = Path.GetDirectoryName(folder.TrimEnd('\\'));
                return parent == null ? folder : WithSlash(parent);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<SyncLocalFile>();
            foreach (var track in _library.Tracks.ToList())
            {
                var root = roots.FirstOrDefault(r => track.Path.StartsWith(r.Match, StringComparison.OrdinalIgnoreCase));
                if (root.Match == null)
                    continue;
                string relative = track.Path[root.Start.Length..].Replace('\\', '/');
                // 別々のライブラリフォルダに同じ相対パスがあると Android 側で 1 つにしかならないので、先の方だけを使う
                if (!seen.Add(relative))
                    continue;

                long size;
                try
                {
                    size = new FileInfo(track.Path).Length;
                }
                catch (IOException)
                {
                    continue; // スキャン後に消えたファイルは候補にしない
                }

                result.Add(new SyncLocalFile
                {
                    FullPath = track.Path,
                    RelativePath = relative,
                    Extension = Path.GetExtension(track.Path).ToLowerInvariant(),
                    FolderKey = FolderKeyOf(relative),
                    Size = size
                });
            }
            return result;
        });

        /// <summary>
        /// 相対パスの「フォルダ単位」。先頭から AppConfig.SyncFolderDepth 段（ファイル名は除く）。
        /// 例: 深さ 1 で "FLAC/Artist/Album/01.flac" → "FLAC"。ルート直下のファイルは ""。
        /// </summary>
        public static string FolderKeyOf(string relativePath)
        {
            string[] parts = relativePath.Split('/');
            int depth = Math.Min(AppConfig.SyncFolderDepth, parts.Length - 1);
            return string.Join('/', parts.Take(depth));
        }

        /// <summary>フォルダを完全パス＋末尾 "\" にそろえる（前方一致で「Music」と「Music2」を取り違えないため）。</summary>
        private static string WithSlash(string folder) => Path.GetFullPath(folder).TrimEnd('\\') + "\\";

        // ───────── 比較 ─────────

        /// <summary>
        /// 選んだ拡張子・フォルダの両方に当てはまる PC のファイルと、Android の一覧を比べて、追加・上書き・削除候補を決める。
        /// 相対パスの大文字小文字は区別しない（Windows も Android の内部ストレージも区別しないため）。
        /// </summary>
        public async Task<SyncPlan> BuildPlanAsync(SyncDevice device, IReadOnlyList<SyncLocalFile> catalog,
            ISet<string> extensions, ISet<string> folders, IReadOnlyList<string> playlistPaths, CancellationToken token)
        {
            var json = await SendJsonAsync(HttpMethod.Get, device.Host, device.Port, "/files", device.Token, null, token);
            var remote = json["files"]?.ToObject<List<SyncRemoteFile>>() ?? new List<SyncRemoteFile>();
            var remoteMap = new Dictionary<string, SyncRemoteFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in remote)
                remoteMap[r.Path] = r;

            bool IsTarget(string extension, string folderKey) => extensions.Contains(extension) && folders.Contains(folderKey);

            var plan = new SyncPlan();
            var selected = catalog.Where(f => IsTarget(f.Extension, f.FolderKey)).ToList();
            var selectedPaths = new HashSet<string>(selected.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            foreach (var file in selected)
            {
                if (!remoteMap.TryGetValue(file.RelativePath, out var r))
                    plan.ToAdd.Add(file);
                else if (r.Size != file.Size)
                    plan.ToUpdate.Add(file); // タグを書き換えた・再変換したなどで中身が変わった
                else
                    plan.Unchanged++;
            }

            // 削除候補は「同期対象の拡張子・フォルダに入るのに PC に無い」ものだけ。対象外のファイル（写真や別形式）には触らない
            foreach (var r in remote)
            {
                string extension = Path.GetExtension(r.Path).ToLowerInvariant();
                if (IsTarget(extension, FolderKeyOf(r.Path)) && !selectedPaths.Contains(r.Path))
                    plan.ToDelete.Add(r);
            }
            plan.ToDelete.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            foreach (string path in playlistPaths)
            {
                token.ThrowIfCancellationRequested();
                plan.Playlists.Add(await ConvertPlaylistAsync(path, catalog, selected));
            }
            return plan;
        }

        /// <summary>
        /// m3u プレイリストを、Android で使う相対パスの並びにする。
        /// 指している曲が同期対象外（例: FLAC を同期しない設定で .flac を指している）のときは、同期する別形式に置き換える:
        ///   1. 同じフォルダにある、同じ名前で拡張子だけ違うファイル
        ///   2. 形式ごとのフォルダ違い（FLAC/… と MP3/…）で、フォルダ単位より下の「アーティスト/アルバム/曲名」が同じファイル
        /// どちらも無ければその曲は入れない。
        /// </summary>
        private async Task<SyncPlaylistPayload> ConvertPlaylistAsync(string playlistPath,
            IReadOnlyList<SyncLocalFile> catalog, IReadOnlyList<SyncLocalFile> selected)
        {
            var byFullPath = catalog.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);
            var selectedSet = new HashSet<SyncLocalFile>(selected);
            // 置き換え 1 用: 拡張子を除いた相対パス → 同期するファイル
            var byStem = new Dictionary<string, SyncLocalFile>(StringComparer.OrdinalIgnoreCase);
            // 置き換え 2 用: フォルダ単位より下で拡張子を除いた部分 → 同期するファイル
            var byTail = new Dictionary<string, SyncLocalFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in selected)
            {
                byStem.TryAdd(StemOf(f.RelativePath), f);
                byTail.TryAdd(TailKeyOf(f), f);
            }

            var doc = await _playlists.LoadAsync(playlistPath);
            var payload = new SyncPlaylistPayload
            {
                Name = Path.GetFileNameWithoutExtension(playlistPath),
                FileName = Path.GetFileName(playlistPath)
            };
            try
            {
                // Android のプレイリスト保存先に PC と同じファイルを置くため、中身をバイトのまま送る
                payload.ContentBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(playlistPath));
            }
            catch (IOException)
            {
                // 読めなければ中身は送らない（Android は Entries の相対パスだけで反映する）
            }

            foreach (var entry in doc.Entries)
            {
                if (!byFullPath.TryGetValue(entry.Track.Path, out var file))
                    continue; // ライブラリに無い曲は Android にも無いので入れない

                SyncLocalFile? target = selectedSet.Contains(file) ? file
                    : byStem.GetValueOrDefault(StemOf(file.RelativePath))
                      ?? byTail.GetValueOrDefault(TailKeyOf(file));
                if (target != null)
                {
                    payload.Entries.Add(target.RelativePath);
                    // ファイルの行（PC の絶対パス）→ Android での相対パス。Android で読み替えに失敗した行の補いに使う
                    payload.Replacements.Add(new SyncPlaylistReplacement { From = entry.Track.Path, To = target.RelativePath });
                }
            }
            return payload;
        }

        private static string StemOf(string relativePath)
        {
            int slash = relativePath.LastIndexOf('/');
            int dot = relativePath.LastIndexOf('.');
            return dot > slash ? relativePath[..dot] : relativePath;
        }

        private static string TailKeyOf(SyncLocalFile file)
        {
            string stem = StemOf(file.RelativePath);
            return file.FolderKey.Length == 0 ? stem : stem[(file.FolderKey.Length + 1)..];
        }

        // ───────── 実行 ─────────

        /// <summary>
        /// 比較結果のとおりに送る。順番は ファイル送信 → （承認されていれば）削除 → プレイリスト → 完了通知。
        /// 完了通知を受けた Android は再スキャンしてプレイリストを反映する。
        /// 途中でキャンセルした場合も、それまでに送ったファイルは Android に残る（次回の比較で「同じ」になる）。
        /// </summary>
        public async Task RunAsync(SyncDevice device, SyncPlan plan, bool deleteRemote,
            IProgress<SyncProgress> progress, CancellationToken token)
        {
            IsRunning = true;
            try
            {
                var files = plan.ToAdd.Concat(plan.ToUpdate).ToList();
                long totalBytes = plan.TransferBytes;
                long doneBytes = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var file = files[i];
                    int doneFiles = i;
                    long baseBytes = doneBytes;
                    progress.Report(new SyncProgress
                    {
                        DoneFiles = doneFiles, TotalFiles = files.Count,
                        DoneBytes = baseBytes, TotalBytes = totalBytes, CurrentName = file.RelativePath
                    });

                    await UploadAsync(device, file, sent => progress.Report(new SyncProgress
                    {
                        DoneFiles = doneFiles, TotalFiles = files.Count,
                        DoneBytes = baseBytes + sent, TotalBytes = totalBytes, CurrentName = file.RelativePath
                    }), token);
                    doneBytes += file.Size;
                }

                if (deleteRemote && plan.ToDelete.Count > 0)
                {
                    var body = new { paths = plan.ToDelete.Select(d => d.Path).ToList() };
                    await SendJsonAsync(HttpMethod.Post, device.Host, device.Port, "/delete", device.Token, body, token);
                }

                await SendJsonAsync(HttpMethod.Put, device.Host, device.Port, "/playlists", device.Token, plan.Playlists, token);
                await SendJsonAsync(HttpMethod.Post, device.Host, device.Port, "/done", device.Token, new { }, token);

                progress.Report(new SyncProgress
                {
                    DoneFiles = files.Count, TotalFiles = files.Count,
                    DoneBytes = totalBytes, TotalBytes = totalBytes
                });
                AppRuntime.AppendLifetimeLog($"sync-done device={device.Name} files={files.Count} delete={(deleteRemote ? plan.ToDelete.Count : 0)} playlists={plan.Playlists.Count}");
            }
            finally
            {
                IsRunning = false;
            }
        }

        /// <summary>1 ファイルを PUT /file?path=… で送る。本文はファイルの中身そのもの（メモリに全部は読まない）。</summary>
        private async Task UploadAsync(SyncDevice device, SyncLocalFile file, Action<long> onSent, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(AppConfig.SyncUploadTimeoutSeconds));

            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                AppConfig.SyncUploadBufferSize, useAsync: true);
            using var content = new StreamContent(new ProgressStream(stream, onSent), AppConfig.SyncUploadBufferSize);
            content.Headers.ContentLength = stream.Length;

            string url = BuildUrl(device.Host, device.Port, "/file?path=" + Uri.EscapeDataString(file.RelativePath));
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
            AddAuth(request, device.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            await EnsureSuccessAsync(response);
        }

        // ───────── HTTP の共通処理 ─────────

        private static string BuildUrl(string host, int port, string pathAndQuery) => $"http://{host}:{port}{pathAndQuery}";

        private void AddAuth(HttpRequestMessage request, string? token)
        {
            request.Headers.Add("X-Client-Id", ClientId);
            if (!string.IsNullOrEmpty(token))
                request.Headers.Add("X-Token", token);
        }

        /// <summary>JSON を送って JSON を受け取る（本文が null なら送らない）。</summary>
        private async Task<JObject> SendJsonAsync(HttpMethod method, string host, int port, string path,
            string? authToken, object? body, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(AppConfig.SyncRequestTimeoutSeconds));

            using var request = new HttpRequestMessage(method, BuildUrl(host, port, path));
            AddAuth(request, authToken);
            if (body != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(body, JsonOut), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, timeout.Token);
            await EnsureSuccessAsync(response);
            string text = await response.Content.ReadAsStringAsync(timeout.Token);
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }

        /// <summary>失敗の応答を、画面に出せる言葉の例外にする（401 = ペアリングが必要）。</summary>
        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
                return;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SyncUnauthorizedException(Strings.Get("Sync_NeedPair"));

            string detail = "";
            try { detail = await response.Content.ReadAsStringAsync(); } catch { }
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {detail}".Trim());
        }

        public void Dispose() => _http.Dispose();

        /// <summary>読んだバイト数を知らせるだけの包み（送信の進み具合を出すため）。</summary>
        private sealed class ProgressStream : Stream
        {
            private readonly Stream _inner;
            private readonly Action<long> _onRead;
            private long _total;

            public ProgressStream(Stream inner, Action<long> onRead)
            {
                _inner = inner;
                _onRead = onRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => Report(_inner.Read(buffer, offset, count));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Report(await _inner.ReadAsync(buffer, cancellationToken));

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Report(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

            private int Report(int read)
            {
                _total += read;
                _onRead(_total);
                return read;
            }
        }
    }

    /// <summary>トークンが無い・取り消された（Android 側で解除された）とき。画面は PIN の入力を促す。</summary>
    public sealed class SyncUnauthorizedException : Exception
    {
        public SyncUnauthorizedException(string message) : base(message) { }
    }
}
