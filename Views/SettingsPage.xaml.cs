using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Services;
using WinUiShared;

namespace MusicPlayer.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;
        private bool _isInitializing;

        public SettingsPage()
        {
            InitializeComponent();
            CompactComboBoxHelper.AttachFitToSelectedText(ThemeComboBox);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isInitializing = true;

            ThemeComboBox.Items.Clear();
            ThemeComboBox.Items.Add(Strings.Get("Settings_Theme_System"));
            ThemeComboBox.Items.Add(Strings.Get("Settings_Theme_Light"));
            ThemeComboBox.Items.Add(Strings.Get("Settings_Theme_Dark"));
            ThemeComboBox.SelectedIndex = (int)_state.Settings.ThemePreference;

            // 名前の並べ方（並びは NameSortMode の値の順）
            NameSortComboBox.Items.Clear();
            NameSortComboBox.Items.Add(Strings.Get("Settings_NameSort_Separate"));
            NameSortComboBox.Items.Add(Strings.Get("Settings_NameSort_Mixed"));
            NameSortComboBox.SelectedIndex = (int)_state.Settings.NameSortMode;
            // ボックスを一番長い項目の幅にそろえる（狭いと開いたリストがはみ出して横にずれて見える）
            CompactComboBoxHelper.FitWidthToWidestItem(NameSortComboBox);
            AutoSaveToggle.IsOn = _state.Settings.PlaylistAutoSave;

            RefreshFolders();
            FfmpegText.Text = TranscodeService.FfmpegPath ?? Strings.Get("Settings_FfmpegMissing");

            _isInitializing = false;
        }

        /// <summary>名前の並べ方を変えたら保存する（今の一覧の並びは変えず、次に並べ替えたときから使う）。</summary>
        private void NameSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || NameSortComboBox.SelectedIndex < 0)
                return;

            var mode = (Models.NameSortMode)NameSortComboBox.SelectedIndex;
            if (mode == _state.Settings.NameSortMode)
                return;
            _state.Settings.NameSortMode = mode;
            _state.Settings.Save();
            // 一覧はここでは並び直さない。次に見出しや並べ替えボタンで並べ替えたときに、新しい並べ方が使われる
            SortService.Mode = mode;
        }

        private void AutoSaveToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;
            _state.Settings.PlaylistAutoSave = AutoSaveToggle.IsOn;
            _state.Settings.Save();
        }

        private void RefreshFolders()
        {
            LibraryFoldersList.ItemsSource = _state.Settings.LibraryFolders.ToList();
            RipFolderText.Text = _state.Settings.RipFolder;
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || ThemeComboBox.SelectedIndex < 0)
                return;

            var preference = (AppThemePreference)ThemeComboBox.SelectedIndex;
            if (preference == _state.Settings.ThemePreference)
                return;

            ThemeService.SetPreference(preference);
            _state.Settings.ThemePreference = preference;
            _state.Settings.Save();
        }

        /// <summary>ライブラリフォルダを追加して、すぐに走査し直す。</summary>
        private void AddLibraryFolderButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => AddLibraryFolderButton_ClickAsync(sender, e), XamlRoot, "AddLibraryFolderButton_Click");

        private async Task AddLibraryFolderButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? folder = await PickerHelper.PickFolderAsync();
            if (folder == null || _state.Settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                return;

            _state.Settings.LibraryFolders.Add(folder);
            _state.Settings.Save();
            // 追加したフォルダの直下に「Playlists」「プレイリスト」があれば、そこのプレイリストを読み込む
            _state.Playlists.ApplyDetectedFolder();
            RefreshFolders();
            await _state.Library.ScanAsync();
        }

        private void RemoveLibraryFolderButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => RemoveLibraryFolderButton_ClickAsync(sender, e), XamlRoot, "RemoveLibraryFolderButton_Click");

        private async Task RemoveLibraryFolderButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string folder)
                return;

            _state.Settings.LibraryFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            _state.Settings.Save();
            _state.Playlists.ApplyDetectedFolder();
            RefreshFolders();
            await _state.Library.ScanAsync();
        }

        private void RescanButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => RescanButton_ClickAsync(sender, e), XamlRoot, "RescanButton_Click");

        private async Task RescanButton_ClickAsync(object sender, RoutedEventArgs e) => await _state.Library.ScanAsync();

        private void ChangeRipFolderButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(() => ChangeRipFolderButton_ClickAsync(sender, e), XamlRoot, "ChangeRipFolderButton_Click");

        private async Task ChangeRipFolderButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            string? folder = await PickerHelper.PickFolderAsync();
            if (folder == null)
                return;

            _state.Settings.RipFolder = folder;
            _state.Settings.Save();
            RefreshFolders();
        }

        /// <summary>
        /// 音楽ファイルを開くアプリとして登録し、Windows の既定のアプリ画面を開く
        /// （既定にするかどうかは Windows の仕様上、その画面でユーザーが選ぶ）。
        /// </summary>
        private void DefaultAppButton_Click(object sender, RoutedEventArgs e) =>
            SafeAsync.Run(() => { FileAssociationService.RegisterAndOpenSettings(); return Task.CompletedTask; }, XamlRoot, "DefaultAppButton_Click");
    }
}
