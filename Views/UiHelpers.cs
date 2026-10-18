using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Microsoft.Windows.Storage.Pickers;

namespace MusicPlayer.Views
{
    /// <summary>
    /// アイコンだけのボタンに、ツールチップと読み上げ用の名前（UI Automation の Name）を同時に付ける。
    /// 名前が無いとスクリーンリーダーや自動テストからボタンを特定できない。
    /// </summary>
    /// <summary>一覧のスクロール操作。</summary>
    public static class ListScroll
    {
        /// <summary>
        /// 一覧を一番上までスクロールする（見出しで並べ替えた直後に呼ぶ）。
        /// 並べ替えの直後はまだ行の配置が終わっていないので、配置が済んでから（優先度 Low で）動かす。
        /// </summary>
        public static void ToTop(ListViewBase list)
        {
            list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (FindScrollViewer(list) is ScrollViewer viewer)
                    viewer.ChangeView(null, 0, null, disableAnimation: true);
                else if (list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[0]);
            });
        }

        /// <summary>ListView の中の ScrollViewer（テンプレートの中にある）を探す。</summary>
        private static ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv)
                    return sv;
                if (FindScrollViewer(child) is ScrollViewer found)
                    return found;
            }
            return null;
        }
    }

    public static class A11y
    {
        public static void Label(FrameworkElement element, string text)
        {
            ToolTipService.SetToolTip(element, text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, text);
        }
    }

    /// <summary>
    /// ファイル選択ダイアログ。Windows App SDK の Microsoft.Windows.Storage.Pickers を使う。
    /// 旧来の Windows.Storage.Pickers（InitializeWithWindow 方式）は、この環境では呼んでもダイアログが出ないまま
    /// 待ち続けることがあったため使わない。新 API はウィンドウ ID を渡すだけでデスクトップアプリから使える。
    /// </summary>
    public static class PickerHelper
    {
        public static Task<string?> PickFolderAsync() => RunAsync(async () =>
        {
            var picker = new FolderPicker(App.Runtime.MainWindowId)
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary
            };
            var result = await picker.PickSingleFolderAsync();
            return result?.Path;
        });

        public static async Task<List<string>> PickFilesAsync(IEnumerable<string> extensions) =>
            await RunAsync<List<string>?>(async () =>
            {
                var picker = new FileOpenPicker(App.Runtime.MainWindowId)
                {
                    SuggestedStartLocation = PickerLocationId.MusicLibrary
                };
                foreach (string ext in extensions)
                    picker.FileTypeFilter.Add(ext);
                var results = await picker.PickMultipleFilesAsync();
                return results.Select(r => r.Path).ToList();
            }) ?? new List<string>();

        public static Task<string?> PickImageAsync() => RunAsync(async () =>
        {
            var picker = new FileOpenPicker(App.Runtime.MainWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            var result = await picker.PickSingleFileAsync();
            return result?.Path;
        });

        /// <summary>
        /// ピッカーが開けなかった場合は、例外を握りつぶさず lifetime.log に残して null を返す。
        /// ボタンのクリック処理（async void）で例外が出ると、原因が分からないまま何も起きなくなるため。
        /// </summary>
        private static async Task<T?> RunAsync<T>(Func<Task<T?>> pick)
        {
            try
            {
                return await pick();
            }
            catch (Exception ex)
            {
                AppRuntime.AppendLifetimeLog($"picker-failed {ex.GetType().Name} {ex.HResult:X8}: {ex.Message}");
                return default;
            }
        }
    }

    /// <summary>
    /// ボタンのクリックなど「イベントから非同期処理を始める」ときの入口。
    /// async void のまま例外が漏れると、XAML の UnhandledException を通らずプロセスごと落ちることがあるため、
    /// ここで必ず受け止めて lifetime.log に記録し、画面にはエラーとして表示する（アプリは落とさない）。
    /// </summary>
    public static class SafeAsync
    {
        /// <param name="action">実行する非同期処理。</param>
        /// <param name="root">エラーダイアログを出す XamlRoot（null ならログだけ）。</param>
        /// <param name="context">ログに残す処理名（どこで失敗したかを後から追えるように）。</param>
        public static async void Run(Func<Task> action, XamlRoot? root = null, string context = "")
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppRuntime.AppendLifetimeLog($"ui-async-failed {context} {ex}");
                if (root == null)
                    return;
                try
                {
                    await Dialogs.ShowErrorAsync(root, ex.Message);
                }
                catch
                {
                    // 別のダイアログが開いている間は ContentDialog を重ねられない。ログは残したので表示は諦める
                }
            }
        }
    }

    /// <summary>よく使うダイアログ（入力・確認・エラー表示）。</summary>
    public static class Dialogs
    {
        /// <summary>1 行入力ダイアログ。キャンセルなら null。</summary>
        public static async Task<string?> PromptTextAsync(XamlRoot root, string title, string initial = "")
        {
            var box = new TextBox { Text = initial, MinWidth = 320 };
            box.SelectAll();
            var dialog = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = Strings.Get("Common_OK"),
                CloseButtonText = Strings.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
        }

        public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primaryText)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = Strings.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = root
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        public static async Task ShowErrorAsync(XamlRoot root, string message)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.Get("Common_Error"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                CloseButtonText = Strings.Get("Common_OK"),
                XamlRoot = root
            };
            await dialog.ShowAsync();
        }
    }

    /// <summary>
    /// 曲の右クリックメニュー（再生 / 次に再生 / キューに追加 / プレイリストに追加 / タグ編集 / 場所を開く）。
    /// ライブラリ・アルバム・アーティスト・プレイリストの各画面で共通に使う。
    /// </summary>
    public static class TrackMenu
    {
        /// <param name="getTracks">メニューを開いた時点で対象となる曲（選択中の曲など）を返す関数。</param>
        /// <param name="playAll">「再生」で再生する曲一覧と開始位置を返す関数（一覧の続きも再生するため）。</param>
        public static MenuFlyout Create(Page page, Func<IReadOnlyList<Track>> getTracks,
            Func<(IReadOnlyList<Track> List, int Start)>? playAll = null)
        {
            var state = App.Runtime.AppState;
            var flyout = new MenuFlyout();

            var play = Item("Menu_Play", "", () =>
            {
                if (playAll != null)
                {
                    var (list, start) = playAll();
                    state.Playback.PlayTracks(list, start);
                }
                else
                {
                    state.Playback.PlayTracks(getTracks());
                }
            });
            var playNext = Item("Menu_PlayNext", "", () => state.Playback.PlayNext(getTracks()));
            var enqueue = Item("Menu_AddToQueue", "", () => state.Playback.Enqueue(getTracks()));

            var addToPlaylist = new MenuFlyoutSubItem
            {
                Text = Strings.Get("Menu_AddToPlaylist"),
                Icon = new FontIcon { Glyph = "" }
            };

            var editTags = Item("Menu_EditTags", "", () =>
                page.Frame.Navigate(typeof(TagEditorPage), getTracks().Select(t => t.Path).ToList()));
            var openFolder = Item("Menu_OpenLocation", "", () =>
            {
                var first = getTracks().FirstOrDefault();
                if (first != null && File.Exists(first.Path))
                    Process.Start("explorer.exe", $"/select,\"{first.Path}\"");
            });

            flyout.Items.Add(play);
            flyout.Items.Add(playNext);
            flyout.Items.Add(enqueue);
            flyout.Items.Add(addToPlaylist);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(editTags);
            flyout.Items.Add(openFolder);

            // プレイリスト一覧はメニューを開くたびに作り直す（追加・削除を反映するため）
            // async ラムダをイベントに直接つなぐと例外でアプリが落ちるので、SafeAsync 経由にする
            flyout.Opening += (_, _) => SafeAsync.Run(async () =>
            {
                addToPlaylist.Items.Clear();
                addToPlaylist.Items.Add(Item("Menu_NewPlaylist", "", async () =>
                {
                    string? name = await Dialogs.PromptTextAsync(page.XamlRoot, Strings.Get("Playlist_NewTitle"));
                    if (name == null)
                        return;
                    string? path = await state.Playlists.CreateAsync(name, PickerHelper.PickFolderAsync);
                    if (path != null)
                        await state.Playlists.AppendAsync(path, getTracks());
                }, page.XamlRoot));

                var playlists = await state.Playlists.ListAsync();
                if (playlists.Count > 0)
                    addToPlaylist.Items.Add(new MenuFlyoutSeparator());
                foreach (var p in playlists)
                {
                    string path = p.Path;
                    var item = new MenuFlyoutItem { Text = p.Name };
                    item.Click += (_, _) => SafeAsync.Run(() => state.Playlists.AppendAsync(path, getTracks()),
                        page.XamlRoot, "menu-append-playlist");
                    addToPlaylist.Items.Add(item);
                }
            }, null, "menu-opening");

            return flyout;
        }

        /// <summary>
        /// 一覧（ListView / GridView）に右クリックメニューを付ける。
        /// 未選択の行を右クリックしたらその行を選択してから開く（エクスプローラーと同じ挙動）。
        /// </summary>
        /// <param name="allItems">一覧の全項目（「再生」で選択行から最後まで続けて再生するため）。</param>
        /// <param name="toTrack">項目 → 曲 の変換（PlaylistEntry など Track 以外の項目に対応。対象外は null）。</param>
        public static void Attach(ListViewBase list, Page page, Func<IReadOnlyList<object>> allItems, Func<object, Track?> toTrack)
        {
            IReadOnlyList<Track> Selected() =>
                list.SelectedItems.Select(toTrack).Where(t => t != null).Select(t => t!).ToList();

            (IReadOnlyList<Track>, int) PlayList()
            {
                var items = allItems();
                var tracks = new List<Track>();
                int start = 0;
                object? first = list.SelectedItems.FirstOrDefault();
                foreach (var item in items)
                {
                    var t = toTrack(item);
                    if (t == null)
                        continue;
                    if (ReferenceEquals(item, first))
                        start = tracks.Count;
                    tracks.Add(t);
                }
                return (tracks, start);
            }

            var flyout = Create(page, Selected, PlayList);
            list.RightTapped += (_, e) =>
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is not object item || toTrack(item) == null)
                    return;

                if (!list.SelectedItems.Contains(item))
                    list.SelectedItem = item;

                flyout.ShowAt(list, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(list) });
                e.Handled = true;
            };
        }

        private static MenuFlyoutItem Item(string key, string glyph, Action onClick)
        {
            var item = new MenuFlyoutItem { Text = Strings.Get(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) =>
            {
                // 再生開始・画面遷移などで例外が出ても、メニュー操作でアプリが落ちないようにする
                try { onClick(); }
                catch (Exception ex) { AppRuntime.AppendLifetimeLog($"menu-failed {key} {ex}"); }
            };
            return item;
        }

        /// <summary>非同期処理を行うメニュー項目（例外は SafeAsync が受け止めてエラー表示する）。</summary>
        private static MenuFlyoutItem Item(string key, string glyph, Func<Task> onClick, XamlRoot root)
        {
            var item = new MenuFlyoutItem { Text = Strings.Get(key), Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => SafeAsync.Run(onClick, root, "menu-" + key);
            return item;
        }
    }

    /// <summary>再生位置スライダーのつまみに「1:23」形式の時間を出すためのコンバーター。</summary>
    public sealed partial class TimeTooltipConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is double seconds ? Track.FormatDuration(seconds) : "";

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }
}
