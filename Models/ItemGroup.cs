using System.Collections.Generic;

namespace MusicPlayer.Models
{
    /// <summary>
    /// 見出し付きの一覧の 1 グループ（例: 見出し「A」と、A で始まるアーティストたち）。
    /// GridView を CollectionViewSource（IsSourceGrouped）で表示するとき、
    /// グループ自身が項目の一覧（List）で、Key が見出しの文字になる。
    /// </summary>
    public sealed class ItemGroup<T> : List<T>
    {
        public ItemGroup(string key) => Key = key;

        /// <summary>見出しの文字（「&amp;」「A」「か」「漢字」「2018」など）。</summary>
        public string Key { get; }
    }
}
