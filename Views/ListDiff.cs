using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MusicPlayer.Views
{
    /// <summary>
    /// ObservableCollection を「目標の並び」に合わせる。Clear して全件入れ直すと ListView が全行を作り直して重いので、
    /// 違うところだけ Move / Insert / RemoveAt する（1 曲動かしただけなら Move 1 回で済む）。
    /// </summary>
    public static class ListDiff
    {
        /// <param name="keyOf">同じ項目かどうかを見分けるキー（再生キューなら ID、プレイリストなら項目そのもの）。</param>
        public static void ApplyOrder<T, TKey>(ObservableCollection<T> list, IReadOnlyList<T> target, Func<T, TKey> keyOf)
            where TKey : notnull
        {
            // 1) 目標に無い項目を外す（後ろから外すと添字がずれない）
            var targetKeys = new HashSet<TKey>();
            foreach (var item in target)
                targetKeys.Add(keyOf(item));
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!targetKeys.Contains(keyOf(list[i])))
                    list.RemoveAt(i);
            }

            // 2) 先頭から順に比べ、違う位置だけ直す。
            //    目標の項目が後ろにあれば Move（行の部品は使い回され、画像の読み直しも起きない）、無ければ新しく Insert
            for (int i = 0; i < target.Count; i++)
            {
                TKey key = keyOf(target[i]);
                if (i < list.Count && EqualityComparer<TKey>.Default.Equals(keyOf(list[i]), key))
                    continue;

                int from = -1;
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (EqualityComparer<TKey>.Default.Equals(keyOf(list[j]), key))
                    {
                        from = j;
                        break;
                    }
                }

                if (from >= 0)
                    list.Move(from, i);
                else
                    list.Insert(i, target[i]);
            }

            // 3) 目標より長ければ余りを外す（同じキーが重複していた場合など）
            while (list.Count > target.Count)
                list.RemoveAt(list.Count - 1);
        }

        /// <summary>2 つの並びのキーが完全に同じか（同じなら UI を触る必要がない）。</summary>
        public static bool SameOrder<TA, TB, TKey>(IReadOnlyList<TA> a, IReadOnlyList<TB> b, Func<TA, TKey> keyA, Func<TB, TKey> keyB)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!EqualityComparer<TKey>.Default.Equals(keyA(a[i]), keyB(b[i])))
                    return false;
            }
            return true;
        }
    }
}
