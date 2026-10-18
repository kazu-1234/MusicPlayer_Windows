using System;
using System.IO;

namespace MusicPlayer
{
    /// <summary>
    /// 後から調整しそうな値（UI サイズ・閾値・間隔・ビットレートなど）をまとめた場所。
    /// 処理中に数値を直書きせず、ここを書き換えるだけで挙動を変えられるようにする。
    /// </summary>
    public static class AppConfig
    {
        // ───────── アプリ識別 ─────────

        /// <summary>%AppData% のフォルダ名・Mutex 名などに使う内部名（表示名は resw の AppName）。</summary>
        public const string AppId = "MusicPlayer";

        /// <summary>%AppData%\MusicPlayer（設定・ライブラリキャッシュ・ジャケットキャッシュの置き場）。</summary>
        public static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);

        // ───────── 既定フォルダ ─────────

        /// <summary>ライブラリとして走査する既定フォルダ（ユーザーの「ミュージック」）。</summary>
        public static string DefaultLibraryFolder =>
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        /// <summary>
        /// ライブラリフォルダの直下にこの名前のフォルダがあれば、プレイリスト用フォルダとして使う（大文字小文字は区別しない）。
        /// 見つからないときに新しく作るのは先頭の名前。
        /// </summary>
        public static readonly string[] PlaylistFolderNames = { "Playlists", "プレイリスト" };

        /// <summary>m3u プレイリストを置く既定フォルダ。</summary>
        public static string DefaultPlaylistFolder => Path.Combine(DefaultLibraryFolder, "Playlists");

        /// <summary>
        /// CD 取り込みの保存先ルート。既存ライブラリの「Music\FLAC\アーティスト\アルバム」構成に合わせ、
        /// この下に「形式名\アーティスト\アルバム」を作る。
        /// </summary>
        public static string DefaultRipFolder => Path.Combine(DefaultLibraryFolder, "Music");

        // ───────── ライブラリ ─────────

        /// <summary>ライブラリに取り込む拡張子（小文字・ドット付き）。</summary>
        public static readonly string[] AudioExtensions =
            { ".flac", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif" };

        /// <summary>
        /// 共演表記の区切りの言葉（「AI Feat. カンナ」の Feat. など）。前後が空白のときだけ区切りにする。大文字小文字は区別しない。
        /// 「and」はグループ名に含まれることが多い（CHAGE and ASKA）ので入れない。
        /// </summary>
        public static readonly string[] ArtistSeparatorWords = { "feat.", "feat", "ft.", "ft", "featuring", "with", "vs.", "vs", "x" };

        /// <summary>
        /// 区切りの言葉のうち「ゲスト参加」を表すもの。この前が主のアーティストとはっきり分かるので、
        /// ほかに手がかりが無くても前側にまとめる（× や &amp; のような対等な共演は、手がかりが無ければまとめない）。
        /// </summary>
        public static readonly string[] ArtistGuestWords = { "feat.", "feat", "ft.", "ft", "featuring", "with" };

        /// <summary>
        /// 共演表記の区切りの記号（どこにあっても区切る）。「/」はグループ名に含まれることがある（DISH//）ので入れない。
        /// </summary>
        public static readonly string[] ArtistSeparatorSymbols = { "×", "＋", "+", "&", "＆", ",", "、", ";" };

        /// <summary>タグ読み取りの並列数。HDD で上げすぎるとシークが増えて逆に遅くなる。</summary>
        public const int ScanParallelism = 4;

        // ───────── ジャケット ─────────

        /// <summary>キャッシュする縮小ジャケットの一辺（px）。グリッドと再生画面の両方で使う。</summary>
        public const uint ArtworkCacheSize = 400;

        /// <summary>ジャケット抽出の同時実行数（スクロール時に CPU を使い切らないため）。</summary>
        public const int ArtworkParallelism = 2;

        /// <summary>タグに画像が無いときに探すフォルダ内の画像ファイル名（拡張子なし、小文字）。</summary>
        public static readonly string[] FolderArtNames = { "folder", "cover", "front", "albumart", "jacket" };

        // ───────── UI サイズ ─────────

        // 再生バーは Windows 11 メディア プレーヤーの下部バーを実測して合わせた寸法（px、拡大率 100% 時。125% の画面写真 ÷ 1.25）

        /// <summary>下部の再生バーの高さ（シークバー段 + 曲情報段）。</summary>
        public const double PlayerBarHeight = 118;

        /// <summary>再生バー左のジャケットの一辺。</summary>
        public const double PlayerBarArtSize = 72;

        /// <summary>再生バーのジャケットの角丸。</summary>
        public const double PlayerBarArtCornerRadius = 4;

        /// <summary>再生バーのジャケットのウィンドウ左端からの距離（WMP はシークバーの時間表示より左に寄せている）。</summary>
        public const double PlayerBarArtLeftMargin = 8;

        /// <summary>再生バーのジャケットと曲名の間隔。</summary>
        public const double PlayerBarArtTextSpacing = 16;

        /// <summary>再生バーの曲名・アーティスト欄の最大幅（超えた分は「…」で省略）。</summary>
        public const double PlayerBarTextMaxWidth = 360;

        /// <summary>再生バーの曲名の文字サイズ（太字）。</summary>
        public const double PlayerBarTitleFontSize = 20;

        /// <summary>再生バーの 2 行目（アーティスト • アルバム）の文字サイズ。</summary>
        public const double PlayerBarArtistFontSize = 14;

        /// <summary>再生バー中央の再生／一時停止ボタンの直径。</summary>
        public const double PlayerBarPlayButtonSize = 50;

        /// <summary>再生バーのその他のアイコンボタン（前へ・次へ・シャッフル等）の一辺。</summary>
        public const double PlayerBarIconButtonSize = 40;

        // ───────── 曲一覧の行 ─────────

        /// <summary>曲一覧 1 行の高さ（ジャケットが入る分だけ広めにする）。</summary>
        public const double TrackRowHeight = 52;

        /// <summary>曲一覧の左端に出すジャケットの一辺。</summary>
        public const double TrackRowArtSize = 40;

        /// <summary>行ジャケットをメモリに置いておく最大枚数（スクロールで戻ったときに読み直さないため）。</summary>
        public const int TrackRowArtMemoryCacheCount = 300;

        /// <summary>アルバムグリッドのタイル幅（ジャケット画像の表示サイズ）。</summary>
        public const double AlbumTileSize = 160;

        /// <summary>再生中画面の大きなジャケットの一辺。</summary>
        public const double NowPlayingArtSize = 320;

        /// <summary>アルバム詳細画面のジャケットの一辺。</summary>
        public const double DetailArtSize = 200;

        /// <summary>プレイリスト・再生キューの「元に戻す（Ctrl+Z）」で遡れる回数。</summary>
        public const int UndoHistoryLimit = 20;

        /// <summary>プレイリストの自動保存で、最後の変更からこの時間（ミリ秒）待って保存する（連続操作を 1 回の保存にまとめる）。</summary>
        public const int PlaylistAutoSaveDelayMs = 800;

        /// <summary>プレイリストを保存する直前の内容を残すフォルダ（この下にプレイリストごとのフォルダを作る）。</summary>
        public static string PlaylistBackupFolder => Path.Combine(AppDataFolder, "playlist-backup");

        /// <summary>プレイリスト 1 つにつき残すバックアップの数（これより古いものは消す）。</summary>
        public const int PlaylistBackupCount = 20;

        // ───────── 再生 ─────────

        /// <summary>再生位置表示の更新間隔（ミリ秒）。短いほど滑らかだが CPU を使う。</summary>
        public const int PositionUpdateIntervalMs = 250;

        /// <summary>「前へ」を押したとき、この秒数より進んでいたら曲の頭に戻す（それ未満なら前の曲へ）。</summary>
        public const double PreviousRestartThresholdSeconds = 3;

        /// <summary>再生バーの音量をマウスホイール 1 ノッチで変える量（0〜100 のうち）。</summary>
        public const double VolumeWheelStep = 5;

        /// <summary>音量の初期値（0〜100）。</summary>
        public const double DefaultVolume = 70;

        // ───────── 変換（ffmpeg） ─────────

        /// <summary>exe と同じフォルダからの ffmpeg の相対パス。</summary>
        public static readonly string FfmpegRelativePath = Path.Combine("ffmpeg", "ffmpeg.exe");

        /// <summary>同時に走らせる変換の数（ffmpeg 自体もマルチスレッドなので控えめにする）。</summary>
        public const int ConvertParallelism = 2;

        /// <summary>MP3 のビットレート（kbps）。並びは 音質優先 / バランス / サイズ優先。</summary>
        public static readonly int[] Mp3Bitrates = { 320, 256, 128 };

        /// <summary>AAC のビットレート（kbps）。並びは 音質優先 / バランス / サイズ優先。</summary>
        public static readonly int[] AacBitrates = { 320, 256, 128 };

        /// <summary>FLAC の圧縮レベル（0〜12）。音質は変わらず、大きいほどファイルが小さく変換が遅い。</summary>
        public const int FlacCompressionLevel = 8;

        // ───────── CD 取り込み ─────────

        /// <summary>1 回の読み取りで要求するセクタ数。2352 byte × 26 = 61,152 byte（多くのドライブの上限 64KB 未満）。</summary>
        public const int CdSectorsPerRead = 26;

        /// <summary>読み取りエラー時の再試行回数。</summary>
        public const int CdReadRetries = 4;

        // ───────── MusicBrainz ─────────

        /// <summary>MusicBrainz API の User-Agent（規約で連絡先付きの UA が必須）。</summary>
        public const string MusicBrainzUserAgent = "MusicPlayer/1.0 ( https://github.com/kazu-1234 )";

        /// <summary>MusicBrainz は 1 秒 1 リクエストまで。少し余裕を持たせる。</summary>
        public const int MusicBrainzIntervalMs = 1100;

        /// <summary>MusicBrainz が「混雑中（503）」を返したとき、やり直すまで待つ時間（ミリ秒）。</summary>
        public const int MusicBrainzBusyRetryDelayMs = 2000;

        /// <summary>
        /// ファイルに埋め込むジャケットの長辺の上限（px）。Windows メディア プレーヤーで取り込んだ曲と同じ 2000px。
        /// Cover Art Archive の原寸画像がこれより大きい場合だけ縮小する（小さい画像は拡大しない）。
        /// </summary>
        public const int EmbeddedCoverMaxSize = 2000;

        /// <summary>埋め込み用に縮小するときの JPEG 画質（0〜1）。</summary>
        public const double EmbeddedCoverJpegQuality = 0.92;

        /// <summary>タグ編集の「オンラインで取得」で表示する候補の最大数。</summary>
        public const int OnlineLookupMaxCandidates = 15;
    }
}
