using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MusicPlayer
{
    /// <summary>
    /// 文字列リソース（ja-JP / en-US）へのアクセス。
    /// 一覧表示で同じ文言（「不明なアーティスト」など）を何千回も引くため、一度読んだ値は覚えておく。
    /// </summary>
    public static class Strings
    {
        private static readonly ResourceLoader Loader = new();
        private static readonly ConcurrentDictionary<string, string> Cache = new();

        public static string Get(string key)
        {
            return Cache.GetOrAdd(key, k => Loader.GetString(k));
        }

        public static string Format(string key, params object[] args)
        {
            string format = Get(key);
            return string.IsNullOrEmpty(format) ? key : string.Format(format, args);
        }
    }
}
