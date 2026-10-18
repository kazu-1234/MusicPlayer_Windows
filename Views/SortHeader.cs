using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Models;

namespace MusicPlayer.Views
{
    /// <summary>
    /// 曲一覧の見出し行（タイトル・アーティスト・アルバム…）を押して並べ替えられるようにする。
    /// 見出し行の中の Button で、Tag に SortKey の名前（"Name" "Artist" など）が書かれたものを列の見出しとして扱う。
    /// - 同じ列をもう一度押すと昇順⇔降順を切り替え、別の列を押すとその列の昇順にする
    /// - 今の並べ替えの列には ▲（昇順）／▼（降順）を付ける
    /// </summary>
    public sealed class SortHeader
    {
        private readonly Func<SortSpec?> _current;
        private readonly Action<SortSpec> _onChanged;

        // 列ごとのボタンの中の文字と、矢印を付ける前の元の文字（x:Uid で読み込まれた見出し名）
        private readonly List<(SortKey Key, TextBlock Text, string Label)> _columns = new();

        /// <param name="header">見出し行（Grid など）。</param>
        /// <param name="current">今の並べ替え。null なら「どの列でもない」（▲▼ を出さない。プレイリストの手動の並び）。</param>
        /// <param name="onChanged">押されたときに新しい並べ替えで呼ばれる。</param>
        public SortHeader(Panel header, Func<SortSpec?> current, Action<SortSpec> onChanged)
        {
            _current = current;
            _onChanged = onChanged;

            foreach (var button in FindButtons(header))
            {
                if (button.Tag is not string tag || !Enum.TryParse(tag, out SortKey key) || button.Content is not TextBlock text)
                    continue;

                _columns.Add((key, text, text.Text));
                button.Click += (_, _) => OnClick(key);
                // 読み上げソフト向けに、見出しが並べ替えボタンであることを名前で伝える
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text.Text);
            }
            Refresh();
        }

        private void OnClick(SortKey key)
        {
            var now = _current();
            var spec = new SortSpec
            {
                Key = key,
                // 同じ列なら向きを反転、別の列なら昇順から
                Descending = now != null && now.Key == key && !now.Descending,
                // 並べ替えた時点の名前の並べ方を記録する（以後、設定を切り替えてもこの並びは変わらない）
                NameMode = Services.SortService.Mode
            };
            _onChanged(spec);
            Refresh();
        }

        /// <summary>見出しの ▲▼ を今の並べ替えに合わせる（外から並びが変わったときにも呼ぶ）。</summary>
        public void Refresh()
        {
            var now = _current();
            foreach (var (key, text, label) in _columns)
            {
                text.Text = now != null && now.Key == key
                    ? $"{label} {(now.Descending ? "▼" : "▲")}"
                    : label;
            }
        }

        private static IEnumerable<Button> FindButtons(DependencyObject parent)
        {
            if (parent is Panel panel)
            {
                // 見出し行は XAML で直接 Button を並べているので、子要素をそのまま見ればよい（まだ描画前でも取れる）
                foreach (var child in panel.Children)
                {
                    if (child is Button b)
                        yield return b;
                }
                yield break;
            }

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                foreach (var b in FindButtons(VisualTreeHelper.GetChild(parent, i)))
                    yield return b;
            }
        }
    }
}
