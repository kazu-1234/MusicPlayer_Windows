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
