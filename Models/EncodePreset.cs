namespace MusicPlayer.Models
{
    /// <summary>変換・CD 取り込みの出力形式。</summary>
    public enum AudioFormat
    {
        Wav,
        Flac,
        Mp3,
        Aac,
        Alac
    }

    /// <summary>
    /// 非可逆形式（MP3 / AAC）の品質プリセット。
    /// 値はそのまま AppConfig.Mp3Bitrates / AacBitrates の添字になる。
    /// </summary>
    public enum QualityPreset
    {
        Quality = 0,
        Balanced = 1,
        Size = 2
    }

    public static class AudioFormatInfo
    {
        /// <summary>出力ファイルの拡張子。AAC と ALAC はどちらも MP4 コンテナ（.m4a）。</summary>
        public static string Extension(AudioFormat format) => format switch
        {
            AudioFormat.Wav => ".wav",
            AudioFormat.Flac => ".flac",
            AudioFormat.Mp3 => ".mp3",
            _ => ".m4a"
        };

        /// <summary>CD 取り込み先の形式フォルダ名（既存ライブラリの FLAC / MP3 / WAV に合わせる）。</summary>
        public static string FolderName(AudioFormat format) => format switch
        {
            AudioFormat.Wav => "WAV",
            AudioFormat.Flac => "FLAC",
            AudioFormat.Mp3 => "MP3",
            AudioFormat.Aac => "AAC",
            _ => "ALAC"
        };

        public static string DisplayName(AudioFormat format) => format switch
        {
            AudioFormat.Wav => "WAV",
            AudioFormat.Flac => "FLAC",
            AudioFormat.Mp3 => "MP3",
            AudioFormat.Aac => "AAC (.m4a)",
            _ => "ALAC (.m4a)"
        };

        /// <summary>可逆（音が劣化しない）形式か。可逆形式には品質プリセットが無い。</summary>
        public static bool IsLossless(AudioFormat format) =>
            format is AudioFormat.Wav or AudioFormat.Flac or AudioFormat.Alac;

        /// <summary>非可逆形式のビットレート（kbps）。可逆形式は 0。</summary>
        public static int Bitrate(AudioFormat format, QualityPreset preset) => format switch
        {
            AudioFormat.Mp3 => AppConfig.Mp3Bitrates[(int)preset],
            AudioFormat.Aac => AppConfig.AacBitrates[(int)preset],
            _ => 0
        };

        /// <summary>プリセットの表示名（例:「音質優先 (320 kbps)」）。</summary>
        public static string PresetDisplayName(AudioFormat format, QualityPreset preset)
        {
            string name = preset switch
            {
                QualityPreset.Quality => Strings.Get("Preset_Quality"),
                QualityPreset.Balanced => Strings.Get("Preset_Balanced"),
                _ => Strings.Get("Preset_Size")
            };
            int kbps = Bitrate(format, preset);
            return kbps > 0 ? $"{name} ({kbps} kbps)" : name;
        }
    }
}
