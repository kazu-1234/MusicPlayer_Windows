using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace MusicPlayer.Views
{
    /// <summary>変換リストの 1 件（進捗と状態を画面に通知する）。</summary>
    public sealed class ConvertItem : INotifyPropertyChanged
    {
        public ConvertItem(string path, string? baseFolder)
        {
            Path = path;
            BaseFolder = baseFolder;
        }

        public string Path { get; }

        /// <summary>フォルダごと追加したときの起点（出力先でもサブフォルダ構成を保つため）。</summary>
        public string? BaseFolder { get; }

        public string FileName => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress))); }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>音声ファイルの形式変換（WAV → MP3 など）。音質優先 / バランス / サイズ優先を切り替えられる。</summary>
    public sealed partial class ConvertPage : Page
    {
        private static readonly AudioFormat[] Formats =
            { AudioFormat.Mp3, AudioFormat.Aac, AudioFormat.Flac, AudioFormat.Alac, AudioFormat.Wav };

        private readonly AppState _state = App.Runtime.AppState;
        private readonly ObservableCollection<ConvertItem> _items = new();
        private CancellationTokenSource? _cts;
        private bool _initializing;

        public ConvertPage()
        {
            InitializeComponent();
            ItemsList.ItemsSource = _items;
            A11y.Label(ClearButton, Strings.Get("Tag_Clear"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _initializing = true;
            FormatCombo.Items.Clear();
            foreach (var f in Formats)
                FormatCombo.Items.Add(AudioFormatInfo.DisplayName(f));
            FormatCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, _state.Settings.ConvertFormat));
            RefreshPresets();
            _initializing = false;

            UpdateOutputText();
            FfmpegBar.IsOpen = !TranscodeService.IsAvailable;
            // ページはキャッシュされるので、変換中に戻ってきた場合は実行中の表示を保つ
            UpdateButtons(running: _cts != null);
        }

        private AudioFormat SelectedFormat => Formats[Math.Max(0, FormatCombo.SelectedIndex)];

        /// <summary>品質の選択肢を作り直す。可逆形式（WAV / FLAC / ALAC）は品質が選べないので無効にする。</summary>
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
            PresetCombo.SelectedIndex = (int)_state.Settings.ConvertPreset;
            PresetCombo.IsEnabled = true;
        }

        private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || FormatCombo.SelectedIndex < 0)
                return;

            _state.Settings.ConvertFormat = SelectedFormat;
            _state.Settings.Save();
            _initializing = true;
            RefreshPresets();
            _initializing = false;
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || PresetCombo.SelectedIndex < 0 || AudioFormatInfo.IsLossless(SelectedFormat))
                return;

            _state.Settings.ConvertPreset = (QualityPreset)PresetCombo.SelectedIndex;
            _state.Settings.Save();
        }

        private void UpdateOutputText()
        {
            string folder = _state.Settings.ConvertOutputFolder;
            OutputBox.Text = folder.Length == 0 ? Strings.Get("Convert_SameFolderText") : folder;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => BrowseButton_ClickAsync(sender, e), XamlRoot, "BrowseButton_Click");

        private async Task BrowseButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? folder = await PickerHelper.PickFolderAsync();
            if (folder == null)
                return;

            _state.Settings.ConvertOutputFolder = folder;
            _state.Settings.Save();
            UpdateOutputText();
        }

        private void SameFolderButton_Click(object sender, RoutedEventArgs e)
        {
            _state.Settings.ConvertOutputFolder = "";
            _state.Settings.Save();
            UpdateOutputText();
        }

        private void AddPaths(IEnumerable<string> paths, string? baseFolder)
        {
            var existing = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths.Where(LibraryService.IsAudioFile))
            {
                if (existing.Add(p))
                    _items.Add(new ConvertItem(p, baseFolder));
            }
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddFilesButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => AddFilesButton_ClickAsync(sender, e), XamlRoot, "AddFilesButton_Click");

        private async Task AddFilesButton_ClickAsync(object sender, RoutedEventArgs e) =>
            AddPaths(await PickerHelper.PickFilesAsync(AppConfig.AudioExtensions), null);

        private void AddFolderButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => AddFolderButton_ClickAsync(sender, e), XamlRoot, "AddFolderButton_Click");

        private async Task AddFolderButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? folder = await PickerHelper.PickFolderAsync();
            if (folder == null)
                return;

            var files = await Task.Run(() => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
            AddPaths(files, folder);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
                return;
            _items.Clear();
            EmptyText.Visibility = Visibility.Visible;
        }

        private void ItemsList_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                e.AcceptedOperation = DataPackageOperation.Copy;
        }

        /// <summary>エクスプローラーからファイル・フォルダをドロップして追加する。</summary>
        private void ItemsList_Drop(object sender, DragEventArgs e) => SafeAsync.Run(() => ItemsList_DropAsync(sender, e), XamlRoot, "ItemsList_Drop");

        private async Task ItemsList_DropAsync(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            foreach (var item in await e.DataView.GetStorageItemsAsync())
            {
                if (item is StorageFolder folder)
                {
                    var files = await Task.Run(() => Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories).ToList());
                    AddPaths(files, folder.Path);
                }
                else
                {
                    AddPaths(new[] { item.Path }, null);
                }
            }
        }

        /// <summary>
        /// 出力先パスを決める。出力フォルダ指定時はフォルダ追加の起点からのサブフォルダ構成を再現する。
        /// 同名ファイルがあれば「名前 (2)」のように別名にして、既存ファイルを上書きしない。
        /// </summary>
        private string OutputPathFor(ConvertItem item, AudioFormat format)
        {
            string root = _state.Settings.ConvertOutputFolder;
            string dir = item.Folder;
            if (root.Length > 0)
            {
                dir = root;
                if (item.BaseFolder != null)
                {
                    string rel = Path.GetRelativePath(Path.GetDirectoryName(item.BaseFolder.TrimEnd('\\')) ?? item.BaseFolder, item.Folder);
                    dir = Path.Combine(root, rel);
                }
            }

            string name = Path.GetFileNameWithoutExtension(item.Path);
            string ext = AudioFormatInfo.Extension(format);
            string path = Path.Combine(dir, name + ext);
            for (int i = 2; File.Exists(path) || string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase); i++)
                path = Path.Combine(dir, $"{name} ({i}){ext}");
            return path;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => StartButton_ClickAsync(sender, e), XamlRoot, "StartButton_Click");

        private async Task StartButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0 || !TranscodeService.IsAvailable)
                return;

            var format = SelectedFormat;
            var preset = _state.Settings.ConvertPreset;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            UpdateButtons(running: true);

            var targets = _items.Where(i => i.Progress < 100).ToList();
            var outputs = new List<string>();
            int done = 0, failed = 0;
            SummaryText.Text = Strings.Format("Convert_Progress", 0, targets.Count);

            // 同時実行数を絞る（ffmpeg 自体もマルチスレッドなので全部同時に走らせない）
            using var gate = new SemaphoreSlim(AppConfig.ConvertParallelism);
            var tasks = targets.Select(async item =>
            {
                await gate.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested)
                        return;

                    item.Status = Strings.Get("Convert_Running");
                    string output = OutputPathFor(item, format);
                    // ライブラリ外のファイルはタグ読み取りが入るため UI スレッドの外で曲情報を得る
                    Track known = await Task.Run(() => _state.Library.GetOrRead(item.Path));
                    var progress = new Progress<double>(p => item.Progress = p * 100);
                    await TranscodeService.ConvertAsync(item.Path, output, format, preset, known.DurationSeconds, progress, token);
                    // タグが無い元ファイルでも、ライブラリがフォルダ名から補った情報を変換後に残す
                    await Task.Run(() => { try { TagService.FillMissing(output, known); } catch { } });
                    item.Progress = 100;
                    item.Status = Strings.Get("Convert_Done");
                    lock (outputs) outputs.Add(output);
                }
                catch (OperationCanceledException)
                {
                    item.Progress = 0;
                    item.Status = Strings.Get("Convert_Canceled");
                }
                catch (Exception ex)
                {
                    item.Progress = 0;
                    item.Status = Strings.Get("Convert_Failed") + ": " + ex.Message;
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    gate.Release();
                    int n = Interlocked.Increment(ref done);
                    DispatcherQueue.TryEnqueue(() => SummaryText.Text = Strings.Format("Convert_Progress", n, targets.Count));
                }
            }).ToList();

            await Task.WhenAll(tasks);

            SummaryText.Text = token.IsCancellationRequested
                ? Strings.Get("Convert_Canceled")
                : Strings.Format("Convert_Finished", outputs.Count, failed);
            _cts.Dispose();
            _cts = null;
            UpdateButtons(running: false);

            // ライブラリのフォルダ内に出力したものはすぐ一覧に出す
            if (outputs.Count > 0)
                await _state.Library.RefreshFilesAsync(outputs);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void UpdateButtons(bool running)
        {
            StartButton.IsEnabled = !running && TranscodeService.IsAvailable;
            CancelButton.IsEnabled = running;
            FormatCombo.IsEnabled = !running;
            PresetCombo.IsEnabled = !running && !AudioFormatInfo.IsLossless(SelectedFormat);
        }
    }
}
