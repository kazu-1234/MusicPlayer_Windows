using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Windows.Storage.Streams;

namespace MusicPlayer.Views
{
    /// <summary>取り込みリストの 1 トラック（曲名・アーティストは画面から直接編集できる）。</summary>
    public sealed class RipTrackItem : INotifyPropertyChanged
    {
        public int Index { get; init; }
        public int Number { get; init; }
        public string NumberText => Number.ToString("00");
        public string LengthText { get; init; } = "";

        public bool IsSelected { get; set; } = true;

        private string _title = "";
        public string Title
        {
            get => _title;
            set { _title = value; Notify(nameof(Title)); }
        }

        private string _artist = "";
        public string Artist
        {
            get => _artist;
            set { _artist = value; Notify(nameof(Artist)); }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; Notify(nameof(Progress)); }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; Notify(nameof(Status)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// CD 取り込み。目次を読む → MusicBrainz で曲名を取得 → 選んだ形式で書き出し → タグとジャケットを書き込む。
    /// CD の読み取り（ドライブ）と変換（CPU）は同時に進められるので、読み終えた曲から順に裏で変換する。
    /// </summary>
    public sealed partial class CdRipPage : Page
    {
        private static readonly AudioFormat[] Formats =
            { AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.Aac, AudioFormat.Wav };

        private readonly AppState _state = App.Runtime.AppState;
        private readonly ObservableCollection<RipTrackItem> _tracks = new();
        private List<CdReleaseInfo> _releases = new();
        private CdDisc? _disc;
        private byte[]? _cover;
        private CancellationTokenSource? _cts;
        private bool _initializing;

        public CdRipPage()
        {
            InitializeComponent();
            TracksList.ItemsSource = _tracks;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_cts != null)
                return; // 取り込み中に戻ってきたときは表示をそのまま保つ

            _initializing = true;
            var selectedDrive = DriveCombo.SelectedItem as string;
            DriveCombo.Items.Clear();
            foreach (char d in CdRipService.GetDrives())
                DriveCombo.Items.Add($"{d}:");
            DriveCombo.SelectedItem = selectedDrive != null && DriveCombo.Items.Contains(selectedDrive) ? selectedDrive : null;
            if (DriveCombo.SelectedIndex < 0 && DriveCombo.Items.Count > 0)
                DriveCombo.SelectedIndex = 0;

            FormatCombo.Items.Clear();
            foreach (var f in Formats)
                FormatCombo.Items.Add(AudioFormatInfo.DisplayName(f));
            FormatCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, _state.Settings.RipFormat));
            RefreshPresets();
            _initializing = false;

            OutputBox.Text = _state.Settings.RipFolder;
            bool hasDrive = DriveCombo.Items.Count > 0;
            ReadButton.IsEnabled = EjectButton.IsEnabled = hasDrive;
            if (!hasDrive)
                ShowMessage(InfoBarSeverity.Warning, Strings.Get("Rip_NoDrive"));
            UpdateRipButton();
        }

        private AudioFormat SelectedFormat => Formats[Math.Max(0, FormatCombo.SelectedIndex)];

        private char? SelectedDrive => (DriveCombo.SelectedItem as string)?[0];

        private void RefreshPresets()
        {
            var format = SelectedFormat;
            PresetCombo.Items.Clear();
            if (AudioFormatInfo.IsLossless(format))
            {
                PresetCombo.Items.Add(Strings.Get("Preset_Lossless"));
                PresetCombo.SelectedIndex = 0;
                PresetCombo.IsEnabled = false;
                return;
            }

            foreach (QualityPreset p in Enum.GetValues<QualityPreset>())
                PresetCombo.Items.Add(AudioFormatInfo.PresetDisplayName(format, p));
            PresetCombo.SelectedIndex = (int)_state.Settings.RipPreset;
            PresetCombo.IsEnabled = true;
        }

        private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FormatCombo.SelectedIndex < 0)
                return;

            _state.Settings.RipFormat = SelectedFormat;
            _state.Settings.Save();
            _initializing = true;
            RefreshPresets();
            _initializing = false;
            UpdateRipButton();
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || PresetCombo.SelectedIndex < 0 || AudioFormatInfo.IsLossless(SelectedFormat))
                return;

            _state.Settings.RipPreset = (QualityPreset)PresetCombo.SelectedIndex;
            _state.Settings.Save();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => BrowseButton_ClickAsync(sender, e), XamlRoot, "BrowseButton_Click");

        private async Task BrowseButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? folder = await PickerHelper.PickFolderAsync();
            if (folder == null)
                return;

            _state.Settings.RipFolder = folder;
            _state.Settings.Save();
            OutputBox.Text = folder;
        }

        private void ShowMessage(InfoBarSeverity severity, string message)
        {
            MessageBar.Severity = severity;
            MessageBar.Message = message;
            MessageBar.IsOpen = true;
        }

        /// <summary>WAV 以外は ffmpeg が必要。ディスクを読んでいて、ffmpeg 条件を満たすときだけ開始できる。</summary>
        private void UpdateRipButton()
        {
            bool needsFfmpeg = SelectedFormat != AudioFormat.Wav;
            RipButton.IsEnabled = _cts == null && _disc != null && (!needsFfmpeg || TranscodeService.IsAvailable);
            if (needsFfmpeg && !TranscodeService.IsAvailable)
                ShowMessage(InfoBarSeverity.Warning, Strings.Get("Rip_FfmpegMissing"));
        }

        // ───────── ディスク読み込み・曲情報 ─────────

        private void ReadButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => ReadButton_ClickAsync(sender, e), XamlRoot, "ReadButton_Click");

        private async Task ReadButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (SelectedDrive is not char drive)
                return;

            MessageBar.IsOpen = false;
            ReadButton.IsEnabled = false;
            _tracks.Clear();
            _releases.Clear();
            ReleaseCombo.Items.Clear();
            ReleaseCombo.IsEnabled = false;
            _cover = null;
            CoverImage.Source = null;
            _disc = null;

            try
            {
                _disc = await CdRipService.ReadDiscAsync(drive);
                for (int i = 0; i < _disc.AudioTracks.Count; i++)
                {
                    _tracks.Add(new RipTrackItem
                    {
                        Index = i,
                        Number = _disc.AudioTracks[i].Number,
                        LengthText = Track.FormatDuration(_disc.TrackSeconds(i)),
                        Title = Strings.Format("Rip_TrackDefault", _disc.AudioTracks[i].Number)
                    });
                }
                EmptyText.Visibility = Visibility.Collapsed;
                DiscBox.Text = "1";
                DiscCountBox.Text = "1";
                await LookupAsync();
            }
            catch (Exception ex)
            {
                ShowMessage(InfoBarSeverity.Error, Strings.Get("Rip_ReadFailed") + " " + ex.Message);
                EmptyText.Visibility = Visibility.Visible;
            }
            finally
            {
                ReadButton.IsEnabled = true;
                UpdateRipButton();
            }
        }

        /// <summary>MusicBrainz で候補を探す。見つからなければ手入力してもらう。</summary>
        private async Task LookupAsync()
        {
            if (_disc == null)
                return;

            try
            {
                _releases = await MusicBrainzClient.LookupAsync(_disc.DiscId, _disc.TocQuery, _disc.AudioTracks.Count, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowMessage(InfoBarSeverity.Warning, Strings.Get("Rip_LookupFailed") + " " + ex.Message);
                return;
            }

            if (_releases.Count == 0)
            {
                ShowMessage(InfoBarSeverity.Informational, Strings.Get("Rip_NotFound"));
                return;
            }

            foreach (var r in _releases)
                ReleaseCombo.Items.Add(r.Display);
            ReleaseCombo.IsEnabled = true;
            ReleaseCombo.SelectedIndex = 0; // → ReleaseCombo_SelectionChanged で反映
        }

        private void ReleaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => SafeAsync.Run(() => ReleaseCombo_SelectionChangedAsync(sender, e), XamlRoot, "ReleaseCombo_SelectionChanged");

        private async Task ReleaseCombo_SelectionChangedAsync(object sender, SelectionChangedEventArgs e)
        {
            int index = ReleaseCombo.SelectedIndex;
            if (index < 0 || index >= _releases.Count)
                return;

            var release = _releases[index];
            AlbumBox.Text = release.Title;
            AlbumArtistBox.Text = release.Artist;
            YearBox.Text = release.Year;
            DiscBox.Text = release.DiscNumber.ToString();
            DiscCountBox.Text = release.DiscCount.ToString();
            // ジャンルも MusicBrainz にあれば自動で入れる（無ければ手入力した値を残す）
            if (release.Genre.Length > 0)
                GenreBox.Text = release.Genre;

            // 曲番号で対応付ける（MusicBrainz 側の曲数がずれていても分かる範囲だけ埋める）
            foreach (var item in _tracks)
            {
                var info = release.Tracks.FirstOrDefault(t => t.Number == item.Index + 1);
                if (info != null)
                {
                    item.Title = info.Title;
                    item.Artist = info.Artist;
                }
            }

            _cover = await MusicBrainzClient.GetCoverAsync(release.ReleaseId, CancellationToken.None);
            if (ReleaseCombo.SelectedIndex == index)
                CoverImage.Source = _cover == null ? null : await ToBitmapAsync(_cover);
        }

        private void ChangeCoverButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => ChangeCoverButton_ClickAsync(sender, e), XamlRoot, "ChangeCoverButton_Click");

        private async Task ChangeCoverButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? path = await PickerHelper.PickImageAsync();
            if (path == null)
                return;

            // 大きすぎる画像は埋め込み用の上限（AppConfig.EmbeddedCoverMaxSize）まで縮める
            _cover = await ImageUtil.FitForEmbeddingAsync(await File.ReadAllBytesAsync(path));
            CoverImage.Source = await ToBitmapAsync(_cover);
        }

        private static async Task<BitmapImage> ToBitmapAsync(byte[] data)
        {
            var image = new BitmapImage { DecodePixelWidth = 224 };
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            return image;
        }

        private void EjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDrive is not char drive)
                return;
            try { CdRipService.Eject(drive); }
            catch (Exception ex) { ShowMessage(InfoBarSeverity.Error, ex.Message); }
        }

        // ───────── 取り込み ─────────

        private void RipButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => RipButton_ClickAsync(sender, e), XamlRoot, "RipButton_Click");

        private async Task RipButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (_disc == null)
                return;

            var targets = _tracks.Where(t => t.IsSelected).ToList();
            if (targets.Count == 0)
                return;

            var format = SelectedFormat;
            var preset = _state.Settings.RipPreset;
            string album = AlbumBox.Text.Trim();
            string albumArtist = AlbumArtistBox.Text.Trim();
            uint.TryParse(YearBox.Text.Trim(), out uint year);
            uint.TryParse(DiscBox.Text.Trim(), out uint discNo);
            uint.TryParse(DiscCountBox.Text.Trim(), out uint discCount);
            string genre = GenreBox.Text.Trim();
            byte[]? cover = _cover;

            // 保存先: <ルート>\<形式>\<アーティスト>\<アルバム>[ [Disc N]]\NN 曲名.ext（既存ライブラリの構成に合わせる）
            string albumFolderName = PlaylistService.SanitizeFileName(album.Length > 0 ? album : Strings.Get("Common_UnknownAlbum"))
                                     + (discCount > 1 ? $" [Disc {discNo}]" : "");
            string folder = Path.Combine(_state.Settings.RipFolder, AudioFormatInfo.FolderName(format),
                PlaylistService.SanitizeFileName(albumArtist.Length > 0 ? albumArtist : Strings.Get("Common_UnknownArtist")),
                albumFolderName);
            string tempDir = Path.Combine(Path.GetTempPath(), "MusicPlayerRip");
            Directory.CreateDirectory(tempDir);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            RipButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            ReadButton.IsEnabled = false;
            MessageBar.IsOpen = false;

            var outputs = new List<string>();
            var encodeTasks = new List<Task>();
            using var encodeGate = new SemaphoreSlim(AppConfig.ConvertParallelism);
            int errors = 0;

            foreach (var item in targets)
            {
                item.Progress = 0;
                item.Status = Strings.Get("Rip_Waiting");
            }

            try
            {
                foreach (var item in targets)
                {
                    token.ThrowIfCancellationRequested();
                    string title = item.Title.Trim().Length > 0 ? item.Title.Trim() : Strings.Format("Rip_TrackDefault", item.Number);
                    string dest = Path.Combine(folder, $"{item.Number:00} {PlaylistService.SanitizeFileName(title)}{AudioFormatInfo.Extension(format)}");
                    string wav = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");
                    bool needsEncode = format != AudioFormat.Wav;

                    // 1) CD → WAV（変換ありなら進捗の前半 70% をこの工程に割り当てる）
                    item.Status = Strings.Get("Rip_Reading");
                    double readShare = needsEncode ? 70 : 100;
                    var readProgress = new Progress<double>(p => item.Progress = p * readShare);
                    int badSectors = await CdRipService.RipTrackToWavAsync(_disc, item.Index, wav, readProgress, token);

                    var edit = new TagEdit
                    {
                        Title = title,
                        Artist = item.Artist.Trim().Length > 0 ? item.Artist.Trim() : albumArtist,
                        AlbumArtist = albumArtist,
                        Album = album,
                        Genre = genre,
                        Year = year,
                        TrackNumber = (uint)item.Number,
                        TrackCount = (uint)_tracks.Count,
                        DiscNumber = discNo,
                        CoverImage = cover
                    };

                    // 2) 変換・タグ書き込み（CD の次の曲の読み取りと並行して裏で行う）
                    await encodeGate.WaitAsync(token);
                    encodeTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            Directory.CreateDirectory(folder);
                            if (needsEncode)
                            {
                                DispatcherQueue.TryEnqueue(() => item.Status = Strings.Get("Rip_Encoding"));
                                var encodeProgress = new Progress<double>(p => DispatcherQueue.TryEnqueue(() => item.Progress = readShare + p * (100 - readShare)));
                                await TranscodeService.ConvertAsync(wav, dest, format, preset, _disc.TrackSeconds(item.Index), encodeProgress, token);
                            }
                            else
                            {
                                File.Move(wav, dest, overwrite: true);
                            }

                            TagService.Write(dest, edit);
                            lock (outputs) outputs.Add(dest);
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                item.Progress = 100;
                                item.Status = badSectors == 0 ? Strings.Get("Convert_Done") : Strings.Format("Rip_DoneWithErrors", badSectors);
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            DispatcherQueue.TryEnqueue(() => item.Status = Strings.Get("Convert_Canceled"));
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errors);
                            DispatcherQueue.TryEnqueue(() => item.Status = Strings.Get("Convert_Failed") + ": " + ex.Message);
                        }
                        finally
                        {
                            try { if (File.Exists(wav)) File.Delete(wav); } catch { }
                            encodeGate.Release();
                        }
                    }));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                errors++;
                ShowMessage(InfoBarSeverity.Error, Strings.Get("Rip_ReadFailed") + " " + ex.Message);
            }

            await Task.WhenAll(encodeTasks);

            if (!MessageBar.IsOpen)
            {
                if (token.IsCancellationRequested)
                    ShowMessage(InfoBarSeverity.Informational, Strings.Get("Convert_Canceled"));
                else
                    ShowMessage(errors == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                        Strings.Format("Rip_Finished", outputs.Count, folder));
            }

            _cts.Dispose();
            _cts = null;
            CancelButton.IsEnabled = false;
            ReadButton.IsEnabled = true;
            UpdateRipButton();

            if (outputs.Count > 0)
                await _state.Library.RefreshFilesAsync(outputs);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    }
}
