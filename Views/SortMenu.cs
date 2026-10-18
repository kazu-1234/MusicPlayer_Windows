using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using MusicPlayer.Models;

namespace MusicPlayer.Views
{
    /// <summary>
    /// 「並べ替え」ボタン（DropDownButton）のメニューを作る。
    /// 上段に基準（名前・アーティスト・追加日など）、下段に昇順／降順をラジオ項目で並べる。
    /// どれかを選ぶと onChanged が新しい SortSpec で呼ばれる。
    /// </summary>
    public static class SortMenu
    {
        /// <summary>基準ごとの表示名の resw キー。曲一覧では Name を「タイトル」と表示する。</summary>
        private static string LabelKey(SortKey key, bool nameIsTitle) => key switch
        {
            SortKey.Name => nameIsTitle ? "Sort_Title" : "Sort_Name",
            SortKey.Artist => "Sort_Artist",
            SortKey.Album => "Sort_Album",
            SortKey.Year => "Sort_Year",
            _ => "Sort_Added"
        };

        /// <param name="nameIsTitle">曲の一覧なら true（「名前」ではなく「タイトル」と表示する）。</param>
        public static void Attach(DropDownButton button, IReadOnlyList<SortKey> keys, bool nameIsTitle,
            Func<SortSpec> current, Action<SortSpec> onChanged)
        {
            var flyout = new MenuFlyout();
            var keyItems = new List<(SortKey Key, RadioMenuFlyoutItem Item)>();
            foreach (var key in keys)
            {
                var item = new RadioMenuFlyoutItem { Text = Strings.Get(LabelKey(key, nameIsTitle)), GroupName = "SortKey" };
                SortKey k = key;
                item.Click += (_, _) =>
                {
                    var spec = current().Clone();
                    spec.NameMode = Services.SortService.Mode; // 並べ替えた時点の名前の並べ方を記録する
                    spec.Key = k;
                    onChanged(spec);
                    UpdateLabel();
                };
                keyItems.Add((key, item));
                flyout.Items.Add(item);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var asc = new RadioMenuFlyoutItem { Text = Strings.Get("Sort_Ascending"), GroupName = "SortDir" };
            var desc = new RadioMenuFlyoutItem { Text = Strings.Get("Sort_Descending"), GroupName = "SortDir" };
            asc.Click += (_, _) => SetDirection(false);
            desc.Click += (_, _) => SetDirection(true);
            flyout.Items.Add(asc);
            flyout.Items.Add(desc);

            // 開くたびに今の設定へチェックを合わせる（別の画面で変えた場合にも追従するため）
            flyout.Opening += (_, _) =>
            {
                var spec = current();
                foreach (var (key, item) in keyItems)
                    item.IsChecked = key == spec.Key;
                asc.IsChecked = !spec.Descending;
                desc.IsChecked = spec.Descending;
            };

            button.Flyout = flyout;
            UpdateLabel();

            void SetDirection(bool descending)
            {
                var spec = current().Clone();
                spec.NameMode = Services.SortService.Mode; // 並べ替えた時点の名前の並べ方を記録する
                spec.Descending = descending;
                onChanged(spec);
                UpdateLabel();
            }

            // ボタンの文字は「並べ替え: 追加日 ↓」のように今の状態を出す
            void UpdateLabel()
            {
                var spec = current();
                if (!keys.Contains(spec.Key))
                    spec = new SortSpec();
                button.Content = $"{Strings.Get("Sort_Button")}: {Strings.Get(LabelKey(spec.Key, nameIsTitle))} {(spec.Descending ? "↓" : "↑")}";
            }
        }

        private static bool Contains(this IReadOnlyList<SortKey> keys, SortKey key)
        {
            foreach (var k in keys)
                if (k == key) return true;
            return false;
        }
    }
}
