using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Views
{
    /// <summary>
    /// Android 版への同期（PC → Android の一方向）。
    /// 流れ: 接続先を選ぶ（初回は PIN でペアリング。以降は保存済みの端末を選ぶだけ）→ 拡張子・フォルダ・プレイリストを選ぶ → 同期開始。
    /// 「同期開始」は比較から転送まで一気に行う。「比較」は送る前に中身（追加・上書き・削除候補）を確かめたいとき用。
    /// </summary>
    public sealed partial class SyncPage : Page
    {
        private readonly AppState _state = App.Runtime.AppState;
        private AndroidSyncService Sync => _state.Sync;

        /// <summary>接続先の候補（ペアリング済み＋今回の探索で見つかった端末）。</summary>
        private readonly ObservableCollection<SyncDevice> _devices = new();

        /// <summary>PC 側の同期候補（ライブラリの全曲）。ページを開くたびに作り直す。</summary>
        private List<SyncLocalFile> _catalog = new();

        /// <summary>直近の比較結果と、そのときの接続先。対象を変えたら捨てる（古い結果で送らないため）。</summary>
        private SyncPlan? _plan;
        private SyncDevice? _planDevice;

        /// <summary>比較・同期・探索の中断用。</summary>
        private CancellationTokenSource? _cts;

        /// <summary>チェックボックスを作り直している間は、設定へ書き戻さない。</summary>
        private bool _isBuilding;

        public SyncPage()
        {
            InitializeComponent();
            DeviceCombo.ItemsSource = _devices;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 同期中に別ページへ行って戻ってきた場合は、表示を作り直さない（進捗表示を消さないため）
            if (Sync.IsRunning)
                return;

            LoadDevices();
            SafeAsync.Run(RebuildTargetsAsync, XamlRoot, "sync-targets");
        }

        // ───────── 接続先 ─────────

        /// <summary>保存済みの端末を候補に入れ、前回使った端末を選ぶ。</summary>
        private void LoadDevices()
        {
            _devices.Clear();
            foreach (var device in _state.Settings.SyncDevices)
                _devices.Add(device);
            DeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.DeviceId == _state.Settings.LastSyncDeviceId)
                                       ?? _devices.FirstOrDefault();
            UpdateDeviceUi();
        }

        private SyncDevice? SelectedDevice => DeviceCombo.SelectedItem as SyncDevice;

        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            InvalidatePlan();
            UpdateDeviceUi();
        }

        /// <summary>選んだ端末がペアリング済みかどうかで、PIN 入力欄と削除ボタンの表示を切り替える。</summary>
        private void UpdateDeviceUi()
        {
            var device = SelectedDevice;
            PinCard.Visibility = device != null && !device.IsPaired ? Visibility.Visible : Visibility.Collapsed;
            ForgetButton.IsEnabled = device != null;
            UpdateStartEnabled(); // ペアリングした・解除されたときに同期開始の押せる／押せないを合わせる
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(async () =>
        {
            ShowDeviceStatus(Strings.Get("Sync_Searching"));
            SearchButton.IsEnabled = false;
            try
            {
                var found = await Sync.DiscoverAsync(CancellationToken.None);
                foreach (var device in found)
                    MergeDevice(device);
                ShowDeviceStatus(found.Count == 0 ? Strings.Get("Sync_NotFound") : Strings.Format("Sync_FoundFormat", found.Count));
                if (SelectedDevice == null && _devices.Count > 0)
                    DeviceCombo.SelectedIndex = 0;
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }, XamlRoot, "sync-discover");

        /// <summary>
        /// 見つかった端末を候補に入れる。ペアリング済みの端末なら IP だけ新しくする
        /// （DHCP で IP が変わっても、探索し直せば同じ端末として使い続けられる）。
        /// </summary>
        private void MergeDevice(SyncDevice found)
        {
            var known = _devices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
            if (known == null)
            {
                _devices.Add(found);
                return;
            }
            if (known.Host != found.Host || known.Name != found.Name)
            {
                known.Host = found.Host;
                known.Name = found.Name;
                known.Port = found.Port;
                if (known.IsPaired)
                    Sync.RememberDevice(known);
                // 表示名（IP 入り）を更新するため入れ直す
                int index = _devices.IndexOf(known);
                bool wasSelected = ReferenceEquals(SelectedDevice, known);
                _devices.RemoveAt(index);
                _devices.Insert(index, known);
                if (wasSelected)
                    DeviceCombo.SelectedItem = known;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(async () =>
        {
            string host = HostBox.Text.Trim();
            if (host.Length == 0)
                return;
            ConnectButton.IsEnabled = false;
            try
            {
                var device = await Sync.GetInfoAsync(host, AppConfig.SyncHttpPort, CancellationToken.None);
                MergeDevice(device);
                DeviceCombo.SelectedItem = _devices.First(d => d.DeviceId == device.DeviceId);
                ShowDeviceStatus("");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ShowDeviceStatus(Strings.Get("Sync_ConnectFailed"));
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }, XamlRoot, "sync-connect");

        private void PairButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(async () =>
        {
            var device = SelectedDevice;
            if (device == null || PinBox.Text.Trim().Length == 0)
                return;
            PairButton.IsEnabled = false;
            try
            {
                await Sync.PairAsync(device, PinBox.Text, CancellationToken.None);
                PinBox.Text = "";
                ShowDeviceStatus(Strings.Get("Sync_Paired"));
                UpdateDeviceUi();
            }
            catch (SyncUnauthorizedException)
            {
                ShowDeviceStatus(Strings.Get("Sync_PairFailed"));
            }
            finally
            {
                PairButton.IsEnabled = true;
            }
        }, XamlRoot, "sync-pair");

        /// <summary>選んだ端末の登録（ペアリング）を消す。</summary>
        private void ForgetButton_Click(object sender, RoutedEventArgs e)
        {
            var device = SelectedDevice;
            if (device == null)
                return;
            Sync.ForgetDevice(device.DeviceId);
            _devices.Remove(device);
            DeviceCombo.SelectedItem = _devices.FirstOrDefault();
            UpdateDeviceUi();
        }

        private void ShowDeviceStatus(string text)
        {
            DeviceStatusText.Text = text;
            DeviceStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ───────── 同期する対象 ─────────

        /// <summary>
        /// ライブラリから拡張子・フォルダの一覧（件数付き）を作り、チェックボックスを並べる。
        /// 初回（まだ一度も選んでいない）は全部にチェックを入れる。
        /// </summary>
        private async Task RebuildTargetsAsync()
        {
            _catalog = await Sync.BuildCatalogAsync();
            var playlists = await _state.Playlists.ListAsync();
            var settings = _state.Settings;
            bool firstTime = !settings.SyncSelectionInitialized;

            _isBuilding = true;
            ExtensionPanel.Children.Clear();
            foreach (var group in _catalog.GroupBy(f => f.Extension).OrderBy(g => g.Key))
            {
                // 表示は「FLAC（1,234）」のように大文字・ドットなし
                string label = Strings.Format("Sync_ItemCountFormat", group.Key.TrimStart('.').ToUpperInvariant(), group.Count());
                bool isChecked = firstTime || settings.SyncExtensions.Contains(group.Key);
                ExtensionPanel.Children.Add(CreateCheck(label, group.Key, isChecked, ExtensionCheck_Changed));
            }

            FolderPanel.Children.Clear();
            var folderGroups = _catalog.GroupBy(f => f.FolderKey).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            // 保存済みのフォルダが古い形式（基準フォルダを変える前の "Music/FLAC" など）なら今のフォルダと合わないので、
            // フォルダだけ初回と同じく全部チェックにする（拡張子・プレイリストの選択は残す）。
            // ライブラリが空のときは戻さない（空の一覧で保存すると、拡張子の選択まで消えてしまうため）
            bool resetFolders = firstTime
                || settings.SyncFolderFormat != AppConfig.SyncFolderFormat && folderGroups.Count > 0;
            foreach (var group in folderGroups)
            {
                string name = group.Key.Length == 0 ? Strings.Get("Sync_RootFolder") : group.Key.Replace('/', '\\');
                string label = Strings.Format("Sync_ItemCountFormat", name, group.Count());
                bool isChecked = resetFolders || settings.SyncFolders.Contains(group.Key, StringComparer.OrdinalIgnoreCase);
                FolderPanel.Children.Add(CreateCheck(label, group.Key, isChecked, FolderCheck_Changed));
            }

            PlaylistPanel.Children.Clear();
            foreach (var playlist in playlists)
            {
                // プレイリストは「除外」で覚える（後から作ったプレイリストは同期する側に入る）
                string fileName = System.IO.Path.GetFileName(playlist.Path);
                bool isChecked = !settings.SyncExcludedPlaylists.Contains(fileName, StringComparer.OrdinalIgnoreCase);
                PlaylistPanel.Children.Add(CreateCheck(playlist.Name, playlist.Path, isChecked, PlaylistCheck_Changed));
            }
            _isBuilding = false;

            if (resetFolders)
                SaveSelection();
            InvalidatePlan();
        }

        private static CheckBox CreateCheck(string label, string tag, bool isChecked, RoutedEventHandler changed)
        {
            var check = new CheckBox { Content = label, Tag = tag, IsChecked = isChecked };
            check.Checked += changed;
            check.Unchecked += changed;
            return check;
        }

        private void ExtensionCheck_Changed(object sender, RoutedEventArgs e) => OnSelectionChanged();
        private void FolderCheck_Changed(object sender, RoutedEventArgs e) => OnSelectionChanged();
        private void PlaylistCheck_Changed(object sender, RoutedEventArgs e) => OnSelectionChanged();

        private void OnSelectionChanged()
        {
            if (_isBuilding)
                return;
            SaveSelection();
            InvalidatePlan();
        }

        /// <summary>チェックの状態を設定に書き戻して保存する（次回もこの選択で開く）。</summary>
        private void SaveSelection()
        {
            var settings = _state.Settings;
            settings.SyncExtensions = CheckedTags(ExtensionPanel.Children, true);
            settings.SyncFolders = CheckedTags(FolderPanel.Children, true);
            settings.SyncFolderFormat = AppConfig.SyncFolderFormat;
            settings.SyncExcludedPlaylists = CheckedTags(PlaylistPanel.Children, false)
                .Select(p => System.IO.Path.GetFileName(p)).ToList();
            settings.SyncSelectionInitialized = true;
            settings.Save();
        }

        private static List<string> CheckedTags(IEnumerable<UIElement> children, bool isChecked) =>
            children.OfType<CheckBox>().Where(c => (c.IsChecked == true) == isChecked).Select(c => (string)c.Tag).ToList();

        // ───────── 比較・同期 ─────────

        /// <summary>
        /// 対象や接続先を変えたら比較結果を捨てる（古い比較結果のまま送らないため）。
        /// 同期開始ボタンは比較結果がなくても押せる（押すと比較からやり直す）。
        /// </summary>
        private void InvalidatePlan()
        {
            _plan = null;
            _planDevice = null;
            DeleteExpander.Visibility = Visibility.Collapsed;
            DeleteCheck.IsChecked = false;
            SummaryText.Text = "";
            ProgressArea.Visibility = Visibility.Collapsed; // 前回の同期の進み具合を残さない
            UpdateStartEnabled();
        }

        /// <summary>
        /// 同期開始ボタンは「ペアリング済みの端末を選んでいて、比較・同期中でない」ときに押せる。
        /// 比較中・同期中かどうかは _cts（中断用の札）があるかで判断する。
        /// </summary>
        private void UpdateStartEnabled()
        {
            var device = SelectedDevice;
            StartButton.IsEnabled = _cts == null && device != null && device.IsPaired;
        }

        /// <summary>比較・同期の前に、端末が選ばれていてペアリング済みかを確かめる（だめなら理由を表示して null）。</summary>
        private SyncDevice? GetReadyDevice()
        {
            var device = SelectedDevice;
            if (device == null)
            {
                SummaryText.Text = Strings.Get("Sync_NoDevice");
                return null;
            }
            if (!device.IsPaired)
            {
                SummaryText.Text = Strings.Get("Sync_NeedPair");
                return null;
            }
            return device;
        }

        /// <summary>
        /// 選んでいる対象で Android 側と比較し、送る・消すファイルの一覧（plan）を作る。
        /// つながらなかったときは、スマホの IP が変わった（DHCP で振り直された）可能性があるので、
        /// 探索で同じ端末（DeviceId が同じ）を探し直し、見つかれば IP を更新して 1 回だけやり直す。
        /// </summary>
        private async Task<SyncPlan> BuildPlanAsync(SyncDevice device, CancellationToken token)
        {
            var extensions = new HashSet<string>(_state.Settings.SyncExtensions, StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(_state.Settings.SyncFolders, StringComparer.OrdinalIgnoreCase);
            var playlistPaths = CheckedTags(PlaylistPanel.Children, true);
            try
            {
                return await Sync.BuildPlanAsync(device, _catalog, extensions, folders, playlistPaths, token);
            }
            catch (HttpRequestException ex) when (IsConnectionError(ex) && !token.IsCancellationRequested)
            {
                SummaryText.Text = Strings.Get("Sync_Reconnecting");
                var found = (await Sync.DiscoverAsync(token)).FirstOrDefault(d => d.DeviceId == device.DeviceId);
                // 見つからない・IP が同じ（＝IP の問題ではない）ならやり直しても同じなので、元のエラーを返す
                if (found == null || found.Host == device.Host)
                    throw;
                // MergeDevice は保存済みの端末オブジェクト（= device）の Host を書き換え、settings.json にも保存する
                MergeDevice(found);
                SummaryText.Text = Strings.Get("Sync_Comparing");
                return await Sync.BuildPlanAsync(device, _catalog, extensions, folders, playlistPaths, token);
            }
        }

        /// <summary>
        /// 接続そのものに失敗した（相手がいない・受信待機がオフ）かどうか。
        /// Android が返したエラー応答（HTTP 500 など）は HttpRequestError.Unknown になるので、ここでは false。
        /// </summary>
        private static bool IsConnectionError(HttpRequestException ex) => ex.HttpRequestError == HttpRequestError.ConnectionError;

        /// <summary>比較・同期の失敗を、利用者に分かる文にする（つながらないときは受信待機の確認を促す）。</summary>
        private string ErrorText(Exception ex)
        {
            string text = Strings.Format("Sync_ErrorFormat", ex.Message);
            return ex is HttpRequestException http && IsConnectionError(http) ? text + "\n" + Strings.Get("Sync_NotFoundHint") : text;
        }

        private void CompareButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(async () =>
        {
            var device = GetReadyDevice();
            if (device == null)
                return;

            InvalidatePlan();
            SummaryText.Text = Strings.Get("Sync_Comparing");
            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                var plan = await BuildPlanAsync(device, _cts.Token);
                ShowPlan(device, plan);
            }
            catch (SyncUnauthorizedException ex)
            {
                // Android 側でペアリングを解除された → PIN を入れ直してもらう
                device.Token = "";
                UpdateDeviceUi();
                SummaryText.Text = ex.Message;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                SummaryText.Text = _cts.IsCancellationRequested ? Strings.Get("Sync_Canceled") : ErrorText(ex);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }, XamlRoot, "sync-compare");

        private void ShowPlan(SyncDevice device, SyncPlan plan)
        {
            _plan = plan;
            _planDevice = device;
            SummaryText.Text = Strings.Format("Sync_SummaryFormat",
                plan.ToAdd.Count, plan.ToUpdate.Count, plan.Unchanged, plan.ToDelete.Count,
                AudioInfoService.FormatSize(plan.TransferBytes), plan.Playlists.Count);

            // 削除候補は一覧で見せる（多すぎるときは先頭だけ表示し、残りは件数で）
            DeletePanel.Children.Clear();
            foreach (var file in plan.ToDelete.Take(AppConfig.SyncPreviewMaxItems))
                DeletePanel.Children.Add(new TextBlock { Text = file.Path, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
            if (plan.ToDelete.Count > AppConfig.SyncPreviewMaxItems)
                DeletePanel.Children.Add(new TextBlock { Text = Strings.Format("Sync_MoreFormat", plan.ToDelete.Count - AppConfig.SyncPreviewMaxItems) });
            DeleteExpander.Visibility = plan.ToDelete.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DeleteExpander.Header = Strings.Format("Sync_DeleteHeaderFormat", plan.ToDelete.Count);
        }

        /// <summary>
        /// 比較から転送までを 1 回で行う。
        /// 同じ端末で比較済みならその結果を使い、まだなら（または対象を変えた後なら）ここで比較する。
        /// 削除候補があるときだけ、消すかどうかを 3 択（削除して同期／削除せずに同期／キャンセル）で聞く。
        /// </summary>
        private void StartButton_Click(object sender, RoutedEventArgs e) => SafeAsync.Run(async () =>
        {
            var device = GetReadyDevice();
            if (device == null)
                return;

            _cts = new CancellationTokenSource();
            SetBusy(true);
            try
            {
                // ── 1. 比較（済んでいなければ） ──
                if (_plan == null || !ReferenceEquals(_planDevice, device))
                {
                    InvalidatePlan();
                    SummaryText.Text = Strings.Get("Sync_Comparing");
                    ShowPlan(device, await BuildPlanAsync(device, _cts.Token));
                }
                var plan = _plan!;

                // ── 2. 削除するか（削除はやり直せないので、候補があるときは必ず聞く） ──
                bool delete = false;
                if (plan.ToDelete.Count > 0)
                {
                    var choice = await AskDeleteAsync(plan.ToDelete.Count);
                    if (choice == ContentDialogResult.None)
                    {
                        SummaryText.Text = Strings.Get("Sync_Canceled");
                        return;
                    }
                    delete = choice == ContentDialogResult.Primary;
                }

                // ── 3. 転送 ──
                ProgressArea.Visibility = Visibility.Visible;
                SyncProgressBar.Value = 0;
                // Progress<T> は作ったスレッド（UI）で報告を受け取るので、そのまま画面を書き換えてよい
                var progress = new Progress<SyncProgress>(ShowProgress);
                await Sync.RunAsync(device, plan, delete, progress, _cts.Token);
                SummaryText.Text = Strings.Get("Sync_Done");
            }
            catch (SyncUnauthorizedException ex)
            {
                // Android 側でペアリングを解除された → PIN を入れ直してもらう
                device.Token = "";
                UpdateDeviceUi();
                SummaryText.Text = ex.Message;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or System.IO.IOException)
            {
                SummaryText.Text = _cts.IsCancellationRequested ? Strings.Get("Sync_Canceled") : ErrorText(ex);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                // 送った後（または途中で止めた後）は Android 側が変わっているので、比較結果は捨てる。
                // 次に同期開始を押せば比較からやり直す
                _plan = null;
                _planDevice = null;
                DeleteExpander.Visibility = Visibility.Collapsed;
                DeleteCheck.IsChecked = false;
                SetBusy(false);
            }
        }, XamlRoot, "sync-run");

        /// <summary>
        /// 削除候補を消すかを聞く。戻り値: Primary = 削除して同期、Secondary = 削除せずに同期、None = キャンセル。
        /// 比較済みで「削除する」にチェックを入れていたときは、既定のボタンを「削除して同期」にする。
        /// </summary>
        private async Task<ContentDialogResult> AskDeleteAsync(int count)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Get("Sync_DeleteChoiceTitle"),
                Content = new TextBlock { Text = Strings.Format("Sync_DeleteChoiceMessage", count), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Strings.Get("Sync_DeleteChoiceDelete"),
                SecondaryButtonText = Strings.Get("Sync_DeleteChoiceKeep"),
                CloseButtonText = Strings.Get("Common_Cancel"),
                DefaultButton = DeleteCheck.IsChecked == true ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
                XamlRoot = XamlRoot
            };
            return await dialog.ShowAsync();
        }

        private void ShowProgress(SyncProgress p)
        {
            // 時間がかかるのは転送なので、バーはバイト数の割合で進める（曲数だと大きなファイルで止まって見える）
            SyncProgressBar.Value = p.TotalBytes > 0 ? 1000.0 * p.DoneBytes / p.TotalBytes : 1000;
            ProgressText.Text = Strings.Format("Sync_ProgressFormat", p.DoneFiles, p.TotalFiles,
                AudioInfoService.FormatSize(p.DoneBytes), AudioInfoService.FormatSize(p.TotalBytes));
            CurrentFileText.Text = p.CurrentName;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        /// <summary>比較・同期中は他の操作を止め、キャンセルだけ押せるようにする。</summary>
        private void SetBusy(bool busy)
        {
            CompareButton.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            DeviceCombo.IsEnabled = !busy;
            SearchButton.IsEnabled = !busy;
            ConnectButton.IsEnabled = !busy;
            ForgetButton.IsEnabled = !busy && SelectedDevice != null;
            UpdateStartEnabled(); // 処理中は _cts があるので押せない。終われば端末の状態で決まる
            foreach (var panel in new Panel[] { ExtensionPanel, FolderPanel, PlaylistPanel })
                foreach (var check in panel.Children.OfType<CheckBox>())
                    check.IsEnabled = !busy;
        }
    }
}
