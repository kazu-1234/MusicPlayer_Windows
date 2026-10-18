using System;
using System.Collections.Generic;
using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Newtonsoft.Json;
using WinUiShared;

namespace MusicPlayer
{
    public class Settings : IUpdateSettings
    {
        /// <summary>デフォルトはシステム連動。</summary>
        public AppThemePreference ThemePreference { get; set; } = AppThemePreference.System;

        public int WindowWidth { get; set; } = 1200;
        public int WindowHeight { get; set; } = 760;

        /// <summary>未保存時は -1。次回起動で位置を復元する。</summary>
        public int WindowX { get; set; } = -1;

        /// <summary>未保存時は -1。次回起動で位置を復元する。</summary>
        public int WindowY { get; set; } = -1;

        /// <summary>前回終了時に最大化されていたか。</summary>
        public bool WindowMaximized { get; set; }

        /// <summary>起動時（初回 MainWindow 表示時）に更新を自動確認する。</summary>
        public bool AutoCheckUpdateOnStartup { get; set; } = true;

        /// <summary>最後に更新確認した UTC 時刻。未確認は null。</summary>
        public DateTime? LastUpdateCheckUtc { get; set; }

        // ───────── ライブラリ・フォルダ ─────────

        /// <summary>ライブラリとして走査するフォルダ。</summary>
        public List<string> LibraryFolders { get; set; } = new() { AppConfig.DefaultLibraryFolder };

        /// <summary>m3u プレイリストのフォルダ。</summary>
        public string PlaylistFolder { get; set; } = AppConfig.DefaultPlaylistFolder;

        /// <summary>ライブラリの形式フィルタ（"" = すべて）。</summary>
        public string LibraryFormatFilter { get; set; } = "";

        /// <summary>ライブラリで最後に開いていた表示（Songs / Albums / Artists）。</summary>
        public string LibraryView { get; set; } = "Albums";

        /// <summary>曲一覧の並べ替え（ライブラリ・アーティスト詳細で共通）。</summary>
        public SortSpec SongsSort { get; set; } = new();

        /// <summary>アルバム一覧の並べ替え。</summary>
        public SortSpec AlbumsSort { get; set; } = new();

        /// <summary>アーティスト一覧の並べ替え。</summary>
        public SortSpec ArtistsSort { get; set; } = new();

        /// <summary>名前の並べ方（かな・漢字を分ける／漢字も読みで混ぜる）。</summary>
        public NameSortMode NameSortMode { get; set; } = NameSortMode.Separate;

        /// <summary>プレイリストの変更を自動で保存する（オフなら「保存」ボタンで保存）。</summary>
        public bool PlaylistAutoSave { get; set; } = true;

        // ───────── 再生 ─────────

        public double Volume { get; set; } = AppConfig.DefaultVolume;
        public bool Shuffle { get; set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.Off;

        // ───────── CD 取り込み ─────────

        public string RipFolder { get; set; } = AppConfig.DefaultRipFolder;
        public AudioFormat RipFormat { get; set; } = AudioFormat.Flac;
        public QualityPreset RipPreset { get; set; } = QualityPreset.Quality;

        // ───────── 変換 ─────────

        public AudioFormat ConvertFormat { get; set; } = AudioFormat.Mp3;
        public QualityPreset ConvertPreset { get; set; } = QualityPreset.Quality;

        /// <summary>変換の出力先。空なら元ファイルと同じフォルダ。</summary>
        public string ConvertOutputFolder { get; set; } = "";

        // ───────── Android 同期 ─────────

        /// <summary>同期する拡張子（小文字・ドット付き）。空なら初回表示時にライブラリにある全部を選ぶ。</summary>
        public List<string> SyncExtensions { get; set; } = new();

        /// <summary>同期するフォルダ（同期の基準フォルダからの相対パス、区切りは "/"）。空なら初回表示時に全部を選ぶ。</summary>
        public List<string> SyncFolders { get; set; } = new();

        /// <summary>
        /// SyncFolders を書いたときのフォルダの形式（AppConfig.SyncFolderFormat）。
        /// 基準フォルダや段数を変えると保存済みの値が今のフォルダと合わなくなるので、違っていたらフォルダの選択だけ初回扱いに戻す。
        /// </summary>
        public int SyncFolderFormat { get; set; }

        /// <summary>同期しないプレイリスト（ファイル名）。新しく作ったプレイリストは同期する側に入るよう「除外」で持つ。</summary>
        public List<string> SyncExcludedPlaylists { get; set; } = new();

        /// <summary>同期画面で、まだ一度も対象を保存していないか（初回は全部を選んだ状態で表示するため）。</summary>
        public bool SyncSelectionInitialized { get; set; }

        /// <summary>ペアリング済みの Android 端末。</summary>
        public List<SyncDevice> SyncDevices { get; set; } = new();

        /// <summary>最後に使った端末の ID。</summary>
        public string LastSyncDeviceId { get; set; } = "";

        /// <summary>この PC を Android 側で見分けるための ID（初回の同期で作る）。</summary>
        public string SyncClientId { get; set; } = "";

        private static string SettingsFilePath =>
            Path.Combine(AppConfig.AppDataFolder, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    // 既定値入りのリストに JSON の値が追記されないよう、コレクションは置き換えで復元する
                    var settings = JsonConvert.DeserializeObject<Settings>(json,
                        new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
                    if (settings != null)
                        return settings;
                }
            }
            catch
            {
                // 破損時はデフォルト設定を返す
            }

            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppConfig.AppDataFolder);
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // 保存失敗は無視（次回は既定値で起動する）
            }
        }
    }
}
