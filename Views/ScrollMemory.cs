using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MusicPlayer.Views
{
    /// <summary>
    /// 「戻る／進む」でページに帰ってきたとき、一覧を離れたときのスクロール位置に戻す。
    /// <para>
    /// ページは戻るたびに一覧を作り直す（ItemsSource を入れ直すと一番上に戻る）ので、
    /// 離れるとき（OnNavigatedFrom）に位置を覚えておき、戻ったとき（OnNavigatedTo の後）に同じ位置へスクロールする。
    /// 覚える単位は「ページの種類 + 表示していたもの（アルバム・アーティスト）+ 一覧の名前」。
    /// アーティスト詳細のようにページが毎回作り直されても位置が残るよう、static に持つ。
    /// </para>
    /// サイドバーから新しく開いたとき（NavigationMode.New）は戻さない（普通に一番上から見せる）。
    /// </summary>
    public static class ScrollMemory
    {
        private static readonly Dictionary<(System.Type Page, object Item, string List), double> Offsets = new();

        /// <summary>今の一覧のスクロール位置を覚える（OnNavigatedFrom で呼ぶ）。</summary>
        public static void Save(Page page, object? item, string listName, ListViewBase list)
        {
            if (ListScroll.FindViewer(list) is not ScrollViewer viewer)
                return;
            // 開いたページの数だけ増えるので、多くなりすぎたら捨てる（古い位置を忘れるだけで動作には影響しない）
            if (Offsets.Count > AppConfig.ScrollMemoryMaxEntries)
                Offsets.Clear();
            Offsets[(page.GetType(), item ?? "", listName)] = viewer.VerticalOffset;
        }

        /// <summary>
        /// 戻る／進むで来たときだけ、覚えておいた位置へスクロールする（一覧に ItemsSource を入れた後に呼ぶ）。
        /// </summary>
        public static void Restore(Page page, NavigationEventArgs e, object? item, string listName, ListViewBase list)
        {
            if (e.NavigationMode is not (NavigationMode.Back or NavigationMode.Forward))
                return;
            if (!Offsets.TryGetValue((page.GetType(), item ?? "", listName), out double offset) || offset <= 0)
                return;
            ScrollWhenReady(list, offset, AppConfig.ScrollRestoreRetries);
        }

        /// <summary>
        /// 入れ直した直後は行の配置（スクロールできる高さの計算）がまだなので、
        /// 目的の位置までスクロールできる高さになるのを待ってから動かす。待ちすぎないよう回数に上限を付ける。
        /// </summary>
        private static void ScrollWhenReady(ListViewBase list, double offset, int retries)
        {
            list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (ListScroll.FindViewer(list) is not ScrollViewer viewer)
                    return;
                if (viewer.ScrollableHeight < offset && retries > 0)
                {
                    ScrollWhenReady(list, offset, retries - 1);
                    return;
                }
                viewer.ChangeView(null, offset, null, disableAnimation: true);
            });
        }
    }
}
