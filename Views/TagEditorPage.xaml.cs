using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Windows.Storage.Streams;

namespace MusicPlayer.Views
{
    /// <summary>タグ編集画面の左側に並べるファイル 1 件。</summary>
    public sealed class TagFileItem
    {
        public TagFileItem(string path) => Path = path;
        public string Path { get; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    }

    /// <summary>
    /// タグ編集。複数ファイルを選ぶと一括編集になり、値が揃っていない欄は空欄＋「(複数の値)」表示。
    /// 触らなかった欄は保存時に書き換えないので、バラバラの曲名などが上書きされることはない。
    /// </summary>
    public sealed partial class TagEditorPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;
        private readonly ObservableCollection<TagFileItem> _files = new();

        /// <summary>入力欄 1 つ分の「読み込み時の値」と「バラバラだったか」。保存時の変更判定に使う。</summary>
        private sealed class Field
        {
            public Field(TextBox box, Func<Track, uint, string> read)
            {
                Box = box;
                Read = read;
            }

            public TextBox Box { get; }
            public Func<Track, uint, string> Read { get; }
            public string Initial { get; set; } = "";
            public bool Mixed { get; set; }

            /// <summary>読み込み時から書き換えられたか（バラバラの欄は何か入力された場合のみ変更扱い）。</summary>
            public bool Changed => Mixed ? Box.Text.Length > 0 : Box.Text != Initial;
        }

        private readonly List<Field> _fields;
        private readonly Field _title, _artist, _albumArtist, _album, _genre, _year, _track, _trackCount, _disc;
        private readonly Field _titleSort, _artistSort, _albumSort;

        private byte[]? _newCover;
        private bool _removeCover;

        // ───────── オンライン取得（MusicBrainz） ─────────

        /// <summary>「オンラインで取得」の候補（CandidateCombo と同じ並び）。</summary>
        private List<OnlineCandidate> _candidates = new();

        /// <summary>選択中のファイルを読み込んだときのタグ（複数ファイルで曲ごとの情報を照合するのに使う）。</summary>
        private List<(string Path, Track Track)> _loaded = new();

        /// <summary>
        /// 複数ファイル選択時、取得したアルバムの曲とトラック番号で照合できたファイルごとの値。
        /// 曲名などはファイルごとに違うので、1 つの入力欄には入れず保存時にファイル別に書く。null なら使わない。
        /// </summary>
        private Dictionary<string, CdTrackInfo>? _perFile;

        public TagEditorPage()
        {
            InitializeComponent();
            FilesList.ItemsSource = _files;
            A11y.Label(ClearButton, Strings.Get("Tag_Clear"));

            static string Num(uint n) => n > 0 ? n.ToString() : "";
            _fields = new List<Field>
            {
                (_title = new Field(TitleBox, (t, _) => t.Title)),
                (_artist = new Field(ArtistBox, (t, _) => t.Artist)),
                (_albumArtist = new Field(AlbumArtistBox, (t, _) => t.AlbumArtist)),
                (_album = new Field(AlbumBox, (t, _) => t.Album)),
                (_genre = new Field(GenreBox, (t, _) => t.Genre)),
                (_year = new Field(YearBox, (t, _) => Num(t.Year))),
                (_track = new Field(TrackBox, (t, _) => Num(t.TrackNumber))),
                (_trackCount = new Field(TrackCountBox, (_, c) => Num(c))),
                (_disc = new Field(DiscBox, (t, _) => Num(t.DiscNumber))),
                // 読み（並べ替え用のタグ）。ほかの欄と同じく、複数選択でバラバラなら「(複数の値)」になる
                (_titleSort = new Field(TitleSortBox, (t, _) => t.TitleSort)),
                (_artistSort = new Field(ArtistSortBox, (t, _) => t.ArtistSort)),
                (_albumSort = new Field(AlbumSortBox, (t, _) => t.AlbumSort))
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // ライブラリ等の「タグを編集」から来た場合は、渡されたファイルを全選択して一括編集にする
            if (e.Parameter is List<string> paths && paths.Count > 0)
            {
                _files.Clear();
                AddPaths(paths);
                FilesList.SelectAll();
            }
            UpdateEmpty();
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            var existing = new HashSet<string>(_files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths.Where(LibraryService.IsAudioFile))
            {
                if (existing.Add(p))
                    _files.Add(new TagFileItem(p));
            }
            UpdateEmpty();
        }

        private void UpdateEmpty() =>
            EmptyText.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void AddFilesButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => AddFilesButton_ClickAsync(sender, e), XamlRoot, "AddFilesButton_Click");

        private async Task AddFilesButton_ClickAsync(object sender, RoutedEventArgs e) =>
            AddPaths(await PickerHelper.PickFilesAsync(AppConfig.AudioExtensions));

        private void AddFolderButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => AddFolderButton_ClickAsync(sender, e), XamlRoot, "AddFolderButton_Click");

        private async Task AddFolderButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? folder = await PickerHelper.PickFolderAsync();
            if (folder == null)
                return;

            var files = await Task.Run(() => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
            AddPaths(files);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            UpdateEmpty();
        }

        private List<string> SelectedPaths =>
            FilesList.SelectedItems.OfType<TagFileItem>().Select(f => f.Path).ToList();

        private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SafeAsync.Run(() => FilesList_SelectionChangedAsync(sender, e), XamlRoot, "FilesList_SelectionChanged");

        private async Task FilesList_SelectionChangedAsync(object sender, SelectionChangedEventArgs e) => await LoadSelectionAsync();

        /// <summary>選択中のファイルのタグを読み、欄ごとに「全ファイル共通の値」か「バラバラ」かを判定して表示する。</summary>
        private async Task LoadSelectionAsync()
        {
            var paths = SelectedPaths;
            ResultBar.IsOpen = false;
            _newCover = null;
            _removeCover = false;
            ResetCandidates();

            bool any = paths.Count > 0;
            FormPanel.IsHitTestVisible = any;
            FormPanel.Opacity = any ? 1 : 0.5;
            SelectionText.Text = any ? Strings.Format("Tag_SelectionCount", paths.Count) : "";
            if (!any)
            {
                foreach (var f in _fields)
                {
                    f.Box.Text = f.Initial = "";
                    f.Box.PlaceholderText = "";
                }
                CoverImage.Source = null;
                return;
            }

            var tags = await Task.Run(() => paths.Select(TagService.ReadForEdit).ToList());
            // 読んでいる間に選択が変わっていたら、この結果は捨てる
            if (!paths.SequenceEqual(SelectedPaths))
                return;
            _loaded = paths.Zip(tags, (path, x) => (path, x.Track)).ToList();

            foreach (var f in _fields)
            {
                var values = tags.Select(x => f.Read(x.Track, x.TrackCount)).Distinct().ToList();
                f.Mixed = values.Count > 1;
                f.Initial = f.Mixed ? "" : values[0];
                f.Box.Text = f.Initial;
                f.Box.PlaceholderText = f.Mixed ? Strings.Get("Tag_MultipleValues") : "";
            }

            byte[]? cover = await Task.Run(() => TagService.ReadCover(paths[0]));
            CoverImage.Source = cover == null ? null : await ToBitmapAsync(cover);
        }

        private static async Task<BitmapImage> ToBitmapAsync(byte[] data)
        {
            var image = new BitmapImage { DecodePixelWidth = 280 };
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            return image;
        }

        private void ChangeCoverButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => ChangeCoverButton_ClickAsync(sender, e), XamlRoot, "ChangeCoverButton_Click");

        private async Task ChangeCoverButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? path = await PickerHelper.PickImageAsync();
            if (path == null)
                return;

            // 大きすぎる画像（数千 px）は埋め込み用の上限まで縮める（ファイルが無駄に大きくならないように）
            _newCover = await ImageUtil.FitForEmbeddingAsync(await File.ReadAllBytesAsync(path));
            _removeCover = false;
            CoverImage.Source = await ToBitmapAsync(_newCover);
        }

        private void RemoveCoverButton_Click(object sender, RoutedEventArgs e)
        {
            _newCover = null;
            _removeCover = true;
            CoverImage.Source = null;
        }

        /// <summary>変更した欄だけを TagEdit に詰めて、選択中の全ファイルへ書き込む。</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => SaveButton_ClickAsync(sender, e), XamlRoot, "SaveButton_Click");

        private async Task SaveButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            var paths = SelectedPaths;
            if (paths.Count == 0)
                return;

            static uint? Number(Field f) => f.Changed ? (uint.TryParse(f.Box.Text.Trim(), out uint n) ? n : 0) : null;
            static string? Text(Field f) => f.Changed ? f.Box.Text : null;

            var edit = new TagEdit
            {
                Title = Text(_title),
                Artist = Text(_artist),
                AlbumArtist = Text(_albumArtist),
                Album = Text(_album),
                Genre = Text(_genre),
                Year = Number(_year),
                TrackNumber = Number(_track),
                TrackCount = Number(_trackCount),
                DiscNumber = Number(_disc),
                TitleSort = Text(_titleSort),
                ArtistSort = Text(_artistSort),
                AlbumSort = Text(_albumSort),
                CoverImage = _newCover,
                RemoveCover = _removeCover
            };

            SaveButton.IsEnabled = false;
            SaveRing.IsActive = true;
            var errors = await Task.Run(() =>
            {
                var list = new List<string>();
                foreach (string p in paths)
                {
                    try { TagService.Write(p, EditFor(p, edit)); }
                    catch (Exception ex) { list.Add($"{Path.GetFileName(p)}: {ex.Message}"); }
                }
                return list;
            });
            SaveRing.IsActive = false;
            SaveButton.IsEnabled = true;

            // ジャケットを変えた場合は縮小キャッシュを作り直させる
            if (_newCover != null || _removeCover)
            {
                foreach (string p in paths)
                {
                    string key = ArtworkCache.KeyFor(p);
                    ArtworkCache.Invalidate(key);
                    TrackArt.Invalidate(key); // 一覧の行ジャケット（メモリ上の画像）も作り直させる
                }
            }

            await _state.Library.RefreshFilesAsync(paths);

            ResultBar.Severity = errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.Title = errors.Count == 0 ? Strings.Format("Tag_Saved", paths.Count) : Strings.Get("Tag_SaveFailed");
            ResultBar.Message = string.Join(Environment.NewLine, errors.Take(5));
            ResultBar.IsOpen = true;

            // 保存後の値を「読み込み時の値」として取り直す
            await LoadSelectionAsync();
            ResultBar.IsOpen = true;
        }

        /// <summary>
        /// 共通の編集内容に、オンライン取得でファイルごとに照合できた曲名・アーティスト・番号を重ねる。
        /// 照合していないファイルは共通の内容のまま書く。
        /// </summary>
        private TagEdit EditFor(string path, TagEdit common)
        {
            if (_perFile == null || !_perFile.TryGetValue(path, out var info))
                return common;

            var edit = common.Clone();
            edit.Title = info.Title;
            edit.Artist = info.Artist;
            edit.TrackNumber = (uint)info.Number;
            edit.DiscNumber = (uint)info.DiscNumber;
            if (info.DiscTrackCount > 0)
                edit.TrackCount = (uint)info.DiscTrackCount;
            return edit;
        }

        private void ResetCandidates()
        {
            _candidates = new List<OnlineCandidate>();
            _perFile = null;
            CandidateCombo.Items.Clear();
            CandidateCombo.IsEnabled = false;
        }

        private void ShowResult(InfoBarSeverity severity, string title, string message = "")
        {
            ResultBar.Severity = severity;
            ResultBar.Title = title;
            ResultBar.Message = message;
            ResultBar.IsOpen = true;
        }

        /// <summary>
        /// 「オンラインで取得」。1 ファイルなら曲名で、複数ならアルバム名で MusicBrainz を検索して候補を並べる。
        /// 先頭の候補を自動で選ぶので、そのまま欄に値が入る（違っていたら候補を選び直す）。
        /// </summary>
        private void LookupButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(LookupAsync, XamlRoot, "tag-lookup");

        private async Task LookupAsync()
        {
            var paths = SelectedPaths;
            if (paths.Count == 0)
                return;

            ResultBar.IsOpen = false;
            ResetCandidates();
            LookupButton.IsEnabled = false;
            LookupRing.IsActive = true;
            try
            {
                List<OnlineCandidate> found;
                if (paths.Count == 1)
                {
                    // 曲名が空ならファイル名で探す
                    string title = TitleBox.Text.Trim();
                    if (title.Length == 0)
                        title = Path.GetFileNameWithoutExtension(paths[0]);
                    string artist = ArtistBox.Text.Trim(), album = AlbumBox.Text.Trim();
                    found = await MusicBrainzClient.SearchRecordingsAsync(title, artist, album, CancellationToken.None);
                    // アルバム名の表記ゆれで 0 件になることがあるので、アルバム名を外してもう一度探す
                    if (found.Count == 0 && album.Length > 0)
                        found = await MusicBrainzClient.SearchRecordingsAsync(title, artist, "", CancellationToken.None);
                }
                else
                {
                    string album = AlbumBox.Text.Trim();
                    if (album.Length == 0)
                    {
                        ShowResult(InfoBarSeverity.Informational, Strings.Get("Tag_LookupNeedAlbum"));
                        return;
                    }
                    string artist = AlbumArtistBox.Text.Trim().Length > 0 ? AlbumArtistBox.Text.Trim() : ArtistBox.Text.Trim();
                    found = await MusicBrainzClient.SearchReleasesAsync(album, artist, CancellationToken.None);
                }

                // 検索している間に選択が変わっていたら、結果は使わない
                if (!paths.SequenceEqual(SelectedPaths))
                    return;
                if (found.Count == 0)
                {
                    ShowResult(InfoBarSeverity.Informational, Strings.Get("Tag_LookupNotFound"));
                    return;
                }

                _candidates = found;
                foreach (var c in found)
                    CandidateCombo.Items.Add(c.Display);
                CandidateCombo.IsEnabled = true;
                CandidateCombo.SelectedIndex = 0; // → CandidateCombo_SelectionChanged で欄に反映
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException
                                           or Newtonsoft.Json.JsonException)
            {
                ShowResult(InfoBarSeverity.Warning, Strings.Get("Tag_LookupFailed"), ex.Message);
            }
            finally
            {
                LookupButton.IsEnabled = true;
                LookupRing.IsActive = false;
            }
        }

        private void CandidateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            SafeAsync.Run(() => ApplyCandidateAsync(CandidateCombo.SelectedIndex), XamlRoot, "tag-apply-candidate");

        /// <summary>選んだ候補のアルバム詳細とジャケットを取得し、入力欄に入れる（保存はまだしない）。</summary>
        private async Task ApplyCandidateAsync(int index)
        {
            if (index < 0 || index >= _candidates.Count)
                return;

            var candidate = _candidates[index];
            var paths = SelectedPaths;
            LookupRing.IsActive = true;
            try
            {
                CdReleaseInfo? release = await MusicBrainzClient.GetReleaseAsync(candidate.ReleaseId, CancellationToken.None);
                // 待っている間に別の候補が選ばれていたら、この結果は捨てる
                if (CandidateCombo.SelectedIndex != index)
                    return;
                if (release == null)
                {
                    ShowResult(InfoBarSeverity.Informational, Strings.Get("Tag_LookupNotFound"));
                    return;
                }

                // アルバム単位の欄（全ファイル共通）
                AlbumBox.Text = release.Title;
                AlbumArtistBox.Text = release.Artist;
                YearBox.Text = release.Year;
                if (release.Genre.Length > 0)
                    GenreBox.Text = release.Genre;

                if (paths.Count == 1)
                {
                    // 検索で見つけた録音（RecordingId）が、アルバムの何曲目かを探す
                    var track = release.Tracks.FirstOrDefault(t => t.RecordingId.Length > 0 && t.RecordingId == candidate.RecordingId)
                                ?? release.Tracks.FirstOrDefault(t => string.Equals(t.Title, TitleBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
                    if (track != null)
                    {
                        TitleBox.Text = track.Title;
                        ArtistBox.Text = track.Artist;
                        TrackBox.Text = track.Number.ToString();
                        TrackCountBox.Text = track.DiscTrackCount > 0 ? track.DiscTrackCount.ToString() : "";
                        DiscBox.Text = track.DiscNumber.ToString();
                    }
                    _perFile = null;
                }
                else
                {
                    // 複数ファイル: 各ファイルの「ディスク番号・トラック番号」でアルバムの曲と対応付ける。
                    // 番号が無いファイルは曲名で探す（どちらでも見つからなければ曲名などは変えない）
                    _perFile = new Dictionary<string, CdTrackInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (path, tag) in _loaded.Where(x => paths.Contains(x.Path)))
                    {
                        int disc = tag.DiscNumber == 0 ? 1 : (int)tag.DiscNumber;
                        var match = (tag.TrackNumber > 0
                                        ? release.Tracks.FirstOrDefault(t => t.DiscNumber == disc && t.Number == tag.TrackNumber)
                                        : null)
                                    ?? release.Tracks.FirstOrDefault(t => string.Equals(t.Title, tag.Title, StringComparison.CurrentCultureIgnoreCase));
                        if (match != null)
                            _perFile[path] = match;
                    }
                    ShowResult(InfoBarSeverity.Informational, Strings.Format("Tag_LookupMatched", _perFile.Count, paths.Count));
                }

                // ジャケット（Cover Art Archive）。見つかれば保存時にファイルへ埋め込む
                byte[]? cover = await MusicBrainzClient.GetCoverAsync(candidate.ReleaseId, CancellationToken.None);
                if (cover != null && CandidateCombo.SelectedIndex == index)
                {
                    _newCover = cover;
                    _removeCover = false;
                    CoverImage.Source = await ToBitmapAsync(cover);
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException
                                           or Newtonsoft.Json.JsonException)
            {
                ShowResult(InfoBarSeverity.Warning, Strings.Get("Tag_LookupFailed"), ex.Message);
            }
            finally
            {
                LookupRing.IsActive = false;
            }
        }
    }
}
