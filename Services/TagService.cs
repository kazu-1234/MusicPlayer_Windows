using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicPlayer.Models;

namespace MusicPlayer.Services
{
    /// <summary>
    /// タグ編集内容。null の項目は「変更しない」を意味する（複数曲の一括編集で、
    /// 値がバラバラな項目に触れなかった場合に元の値を保つため）。
    /// </summary>
    public sealed class TagEdit
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? AlbumArtist { get; set; }
        public string? Album { get; set; }
        public string? Genre { get; set; }
        public uint? TrackNumber { get; set; }
        public uint? TrackCount { get; set; }
        public uint? DiscNumber { get; set; }
        public uint? Year { get; set; }

        // 並べ替え用の読み（TITLESORT / ARTISTSORT / ALBUMSORT）。難読の曲名を正しい位置に並べるため、ひらがなで入れる
        public string? TitleSort { get; set; }
        public string? ArtistSort { get; set; }
        public string? AlbumSort { get; set; }

        /// <summary>新しいジャケット画像（JPEG / PNG のバイト列）。null なら変更しない。</summary>
        public byte[]? CoverImage { get; set; }

        /// <summary>true ならジャケットを削除する（CoverImage より優先）。</summary>
        public bool RemoveCover { get; set; }

        /// <summary>ファイルごとに一部の項目だけ変えたいときの複製（浅いコピー。画像のバイト列は共有する）。</summary>
        public TagEdit Clone() => (TagEdit)MemberwiseClone();
    }

    /// <summary>
    /// TagLib# を使った音楽タグの読み書き。
    /// MP3 = ID3v2、FLAC = Vorbis Comment、M4A = MP4 タグ、WAV = ID3v2 + RIFF INFO を扱う。
    /// </summary>
    public static class TagService
    {
        /// <summary>
        /// ファイルからタグと長さを読む。壊れたファイルでも例外にせず、ファイル名だけの Track を返す
        /// （ライブラリ走査を 1 ファイルの失敗で止めないため）。
        /// </summary>
        public static Track ReadTrack(string path)
        {
            var info = new FileInfo(path);
            var track = new Track
            {
                Path = path,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                FileSize = info.Length,
                // 「追加順」の並べ替えに使う（ファイルがフォルダに置かれた日時）
                AddedTicks = info.CreationTimeUtc.Ticks
            };

            try
            {
                // PictureLazy: ジャケット画像のバイト列を読まずに済ませ、走査を速くする
                using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
                var tag = file.Tag;
                track.Title = tag.Title?.Trim() ?? "";
                track.Artist = JoinNames(tag.Performers);
                track.AlbumArtist = JoinNames(tag.AlbumArtists);
                track.Album = tag.Album?.Trim() ?? "";
                track.Genre = JoinNames(tag.Genres);
                track.TrackNumber = tag.Track;
                track.DiscNumber = tag.Disc;
                track.Year = tag.Year;
                track.DurationSeconds = file.Properties?.Duration.TotalSeconds ?? 0;

                // 並べ替え用の読み（WMP などが TITLESORT / ARTISTSORT 等に書き込む）。無ければ空のまま
                track.TitleSort = tag.TitleSort?.Trim() ?? "";
                track.ArtistSort = JoinNames(tag.PerformersSort);
                track.AlbumSort = tag.AlbumSort?.Trim() ?? "";

                if (file is TagLib.Riff.File)
                    FixRiffInfoEncoding(file, track);
            }
            catch
            {
                // 対応外・破損ファイル。表示はファイル名で行う。
            }

            return track;
        }

        /// <summary>タグ編集画面用に、編集可能な項目すべて（曲数を含む）を読む。</summary>
        public static (Track Track, uint TrackCount) ReadForEdit(string path)
        {
            Track track = ReadTrack(path);
            uint count = 0;
            try
            {
                using var file = TagLib.File.Create(path, TagLib.ReadStyle.PictureLazy);
                count = file.Tag.TrackCount;
            }
            catch
            {
            }

            return (track, count);
        }

        /// <summary>埋め込みジャケット（最初の 1 枚、表紙を優先）のバイト列を返す。無ければ null。</summary>
        public static byte[]? ReadCover(string path)
        {
            try
            {
                using var file = TagLib.File.Create(path);
                var pictures = file.Tag.Pictures;
                if (pictures == null || pictures.Length == 0)
                    return null;

                var front = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
                return front.Data?.Data;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>編集内容をファイルへ書き込む。失敗したら例外を投げる（呼び出し側でファイル名付きで表示する）。</summary>
        public static void Write(string path, TagEdit edit)
        {
            using var file = TagLib.File.Create(path);
            EnsureWritableTags(file);
            var tag = file.Tag;

            if (edit.Title != null) tag.Title = NullIfEmpty(edit.Title);
            if (edit.Artist != null) tag.Performers = SplitNames(edit.Artist);
            if (edit.AlbumArtist != null) tag.AlbumArtists = SplitNames(edit.AlbumArtist);
            if (edit.Album != null) tag.Album = NullIfEmpty(edit.Album);
            if (edit.Genre != null) tag.Genres = SplitNames(edit.Genre);
            if (edit.TrackNumber != null) tag.Track = edit.TrackNumber.Value;
            if (edit.TrackCount != null) tag.TrackCount = edit.TrackCount.Value;
            if (edit.DiscNumber != null) tag.Disc = edit.DiscNumber.Value;
            if (edit.Year != null) tag.Year = edit.Year.Value;
            // 読み（並べ替え用のタグ）。空欄で保存したら消す
            if (edit.TitleSort != null) tag.TitleSort = NullIfEmpty(edit.TitleSort);
            if (edit.ArtistSort != null) tag.PerformersSort = SplitNames(edit.ArtistSort);
            if (edit.AlbumSort != null) tag.AlbumSort = NullIfEmpty(edit.AlbumSort);

            if (edit.RemoveCover)
            {
                tag.Pictures = Array.Empty<TagLib.IPicture>();
            }
            else if (edit.CoverImage != null)
            {
                var picture = new TagLib.Picture(new TagLib.ByteVector(edit.CoverImage))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = DetectImageMime(edit.CoverImage)
                };
                tag.Pictures = new TagLib.IPicture[] { picture };
            }

            file.Save();
        }

        /// <summary>
        /// 変換元のタグとジャケットを変換先へ丸ごとコピーする（ffmpeg のメタデータ引き継ぎの補正）。
        /// 形式ごとにタグの仕組みが違い ffmpeg では落ちる項目があるため、TagLib で揃え直す。
        /// </summary>
        public static void CopyTags(string sourcePath, string destPath)
        {
            using var src = TagLib.File.Create(sourcePath);
            using var dst = TagLib.File.Create(destPath);
            EnsureWritableTags(dst);
            // CopyTo の第 2 引数 true = 既存の値も上書きする
            src.Tag.CopyTo(dst.Tag, true);
            if (src.Tag.Pictures is { Length: > 0 })
                dst.Tag.Pictures = src.Tag.Pictures;

            // Shift_JIS の INFO タグを持つ WAV は、そのままコピーすると化けた文字が移るので、復元済みの値で上書きする
            if (src is TagLib.Riff.File)
            {
                Track fixedTrack = ReadTrack(sourcePath);
                if (fixedTrack.Title.Length > 0) dst.Tag.Title = fixedTrack.Title;
                if (fixedTrack.Artist.Length > 0) dst.Tag.Performers = SplitNames(fixedTrack.Artist);
                if (fixedTrack.AlbumArtist.Length > 0) dst.Tag.AlbumArtists = SplitNames(fixedTrack.AlbumArtist);
                if (fixedTrack.Album.Length > 0) dst.Tag.Album = fixedTrack.Album;
                if (fixedTrack.Genre.Length > 0) dst.Tag.Genres = SplitNames(fixedTrack.Genre);
            }
            dst.Save();
        }

        /// <summary>
        /// 変換先で空になっている項目だけを、ライブラリの曲情報で埋める。
        /// タグの無い WAV でも、ライブラリがフォルダ名から補ったアーティスト・アルバムを変換後のファイルに残すため。
        /// </summary>
        public static void FillMissing(string path, Track known)
        {
            using var file = TagLib.File.Create(path);
            EnsureWritableTags(file);
            var tag = file.Tag;
            bool changed = false;

            if (string.IsNullOrWhiteSpace(tag.Title) && known.Title.Length > 0) { tag.Title = known.Title; changed = true; }
            if (string.IsNullOrWhiteSpace(tag.FirstPerformer) && known.Artist.Length > 0) { tag.Performers = SplitNames(known.Artist); changed = true; }
            if (string.IsNullOrWhiteSpace(tag.Album) && known.Album.Length > 0) { tag.Album = known.Album; changed = true; }
            if (tag.Track == 0 && known.TrackNumber > 0) { tag.Track = known.TrackNumber; changed = true; }

            if (changed)
                file.Save();
        }

        /// <summary>
        /// 古い Windows のソフトで作った WAV は、RIFF INFO タグ（曲名・アーティスト等）を Shift_JIS で書いている。
        /// TagLib はこれを UTF-8 として読むため文字化けする（不正なバイトが U+FFFD「�」になる）。
        /// 化けた項目だけ、INFO の生バイトを読み直して正しい文字コードで復元する。
        /// </summary>
        private static void FixRiffInfoEncoding(TagLib.File file, Track track)
        {
            if (file.GetTag(TagLib.TagTypes.RiffInfo, false) is not TagLib.Riff.InfoTag info)
                return;

            // INFO の 4 文字 ID はソフトによって使い方が違うため、候補を順に試す。
            // INAM = 曲名 / IART = アーティスト / ISTR = 出演者 / IAAR = アルバムアーティスト / IPRD = アルバム / IGNR = ジャンル
            track.Title = Fix(track.Title, "INAM");
            track.Artist = Fix(track.Artist, "IART", "ISTR");
            track.AlbumArtist = Fix(track.AlbumArtist, "IAAR", "IART");
            track.Album = Fix(track.Album, "IPRD");
            track.Genre = Fix(track.Genre, "IGNR");

            // 化けていない値はそのまま。化けていたら候補 ID から最初に読めたものを使う
            string Fix(string value, params string[] ids) =>
                value.Contains('�')
                    ? ids.Select(id => DecodeInfo(info, id)).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? value
                    : value;
        }

        /// <summary>INFO の生バイトを、UTF-8 として正しければ UTF-8、そうでなければ Shift_JIS で文字列にする。</summary>
        private static string? DecodeInfo(TagLib.Riff.InfoTag info, string id)
        {
            var values = info.GetValues(TagLib.ByteVector.FromString(id, TagLib.StringType.Latin1));
            if (values == null || values.Count == 0)
                return null;

            byte[] raw = values[0].Data;
            // INFO の文字列は末尾に NUL（0x00）が付くので取り除く
            int length = Array.IndexOf(raw, (byte)0);
            if (length < 0) length = raw.Length;

            try
            {
                return new System.Text.UTF8Encoding(false, true).GetString(raw, 0, length).Trim();
            }
            catch (System.Text.DecoderFallbackException)
            {
                return ShiftJis.GetString(raw, 0, length).Trim();
            }
        }

        /// <summary>Shift_JIS（コードページ 932）。.NET では登録しないと使えないため、初回使用時に登録する。</summary>
        private static System.Text.Encoding ShiftJis
        {
            get
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding(932);
            }
        }

        /// <summary>
        /// WAV は既定でタグ領域を持たないことがあるため、ID3v2 と RIFF INFO を作っておく。
        /// これをしないと Tag への代入が保存されない。
        /// </summary>
        private static void EnsureWritableTags(TagLib.File file)
        {
            if (file is TagLib.Riff.File)
            {
                file.GetTag(TagLib.TagTypes.Id3v2, true);
                file.GetTag(TagLib.TagTypes.RiffInfo, true);
            }
        }

        /// <summary>複数アーティストを「; 」区切りの 1 文字列にまとめる（表示・検索用）。</summary>
        private static string JoinNames(string[]? names) =>
            names == null ? "" : string.Join("; ", DistinctNames(names));

        /// <summary>
        /// 名前の一覧から重複を除く。FLAC などでは ARTIST が同じ値で何個も入っているファイルがあり
        /// （タグ付けソフトの癖）、そのままつなぐと「A; A; A…」と並んでしまうため。
        /// 1 つの値に「;」区切りで複数入っている場合も分けてから比べる。大文字小文字は区別せず、最初に出た順を保つ。
        /// </summary>
        private static IEnumerable<string> DistinctNames(IEnumerable<string> names) =>
            names.Where(n => n != null)
                 .SelectMany(n => n.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                 .Where(n => n.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>「; 」区切りの入力を配列に戻す。空なら空配列（= タグ削除）。</summary>
        private static string[] SplitNames(string value) =>
            DistinctNames(new[] { value }).ToArray();

        private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>先頭のマジックナンバーで PNG / JPEG を判定する。</summary>
        private static string DetectImageMime(byte[] data) =>
            data.Length > 4 && data[0] == 0x89 && data[1] == 0x50 ? "image/png" : "image/jpeg";
    }
}
