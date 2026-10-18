using System.Collections.Generic;

namespace MusicPlayer.Models
{
    /// <summary>ペアリング済み（またはこれからペアリングする）Android 端末。settings.json に保存する。</summary>
    public sealed class SyncDevice
    {
        /// <summary>Android 側が作った端末 ID（IP が変わっても同じ端末と分かるようにする）。</summary>
        public string DeviceId { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>最後に接続できた IP アドレス（DHCP で変わることがあるので、探索で見つかったら更新する）。</summary>
        public string Host { get; set; } = "";

        public int Port { get; set; } = AppConfig.SyncHttpPort;

        /// <summary>ペアリングで受け取った認証用トークン。空ならまだペアリングしていない。</summary>
        public string Token { get; set; } = "";

        public bool IsPaired => !string.IsNullOrEmpty(Token);

        /// <summary>コンボボックスに出す表示名。</summary>
        public override string ToString() => string.IsNullOrEmpty(Name) ? Host : $"{Name} ({Host})";
    }

    /// <summary>同期の候補になる PC 側の 1 ファイル。</summary>
    public sealed class SyncLocalFile
    {
        public string FullPath { get; init; } = "";

        /// <summary>PC の「ミュージック」からの相対パス（区切りは "/"）。Android 側の「同期先フォルダ」からの相対パスと同じになる。</summary>
        public string RelativePath { get; init; } = "";

        /// <summary>小文字・ドット付きの拡張子。</summary>
        public string Extension { get; init; } = "";

        /// <summary>同期画面の「フォルダ」の単位（相対パスの先頭から AppConfig.SyncFolderDepth 段まで）。</summary>
        public string FolderKey { get; init; } = "";

        public long Size { get; init; }
    }

    /// <summary>Android 側にある 1 ファイル（/files の結果）。</summary>
    public sealed class SyncRemoteFile
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
    }

    /// <summary>「比較」の結果。同期開始でこのとおりに送る・消す。</summary>
    public sealed class SyncPlan
    {
        /// <summary>Android に無いので送るファイル。</summary>
        public List<SyncLocalFile> ToAdd { get; } = new();

        /// <summary>Android にあるがサイズが違うので上書きするファイル。</summary>
        public List<SyncLocalFile> ToUpdate { get; } = new();

        /// <summary>同じなので何もしないファイルの数。</summary>
        public int Unchanged { get; set; }

        /// <summary>PC に無く、同期対象（拡張子・フォルダ）に入っている Android 側のファイル（削除候補）。</summary>
        public List<SyncRemoteFile> ToDelete { get; } = new();

        /// <summary>Android に送るプレイリスト（相対パスに変換済み）。</summary>
        public List<SyncPlaylistPayload> Playlists { get; } = new();

        public long TransferBytes
        {
            get
            {
                long total = 0;
                foreach (var f in ToAdd) total += f.Size;
                foreach (var f in ToUpdate) total += f.Size;
                return total;
            }
        }
    }

    /// <summary>Android へ送るプレイリスト 1 つ（JSON の形そのもの）。</summary>
    public sealed class SyncPlaylistPayload
    {
        public string Name { get; set; } = "";

        /// <summary>同期先フォルダからの相対パス（"/" 区切り）の並び。</summary>
        public List<string> Entries { get; set; } = new();

        /// <summary>PC のプレイリストのファイル名（例 "Music 1.m3u"）。Android のプレイリスト保存先にこの名前で置く（同名は上書き）。</summary>
        public string FileName { get; set; } = "";

        /// <summary>
        /// PC のプレイリストファイルの中身（バイト列そのままを Base64 にしたもの）。
        /// 文字コードも含めて PC と同じファイルを Android に置くため、文字列にせずバイトで送る。読めなかったときは ""。
        /// </summary>
        public string ContentBase64 { get; set; } = "";

        /// <summary>
        /// PC の曲の絶対パス → Android での相対パス。
        /// Android はファイルの中身（PC の絶対パス）をファイル名などで読み替えるが、見つからない曲（同期しない形式など）はこれで置き換える。
        /// Dictionary にしないのは、JSON の camelCase 設定がキー（"C:\…"）の先頭まで小文字にしてしまうため。
        /// </summary>
        public List<SyncPlaylistReplacement> Replacements { get; set; } = new();
    }

    /// <summary>プレイリストの 1 行の置き換え（PC の絶対パス → Android での相対パス）。</summary>
    public sealed class SyncPlaylistReplacement
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    /// <summary>同期中の進み具合（画面表示用）。</summary>
    public sealed class SyncProgress
    {
        public int DoneFiles { get; init; }
        public int TotalFiles { get; init; }
        public long DoneBytes { get; init; }
        public long TotalBytes { get; init; }
        public string CurrentName { get; init; } = "";
    }
}
