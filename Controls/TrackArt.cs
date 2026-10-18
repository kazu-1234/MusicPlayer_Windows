using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Services;

namespace MusicPlayer.Controls
{
    /// <summary>
    /// 曲一覧の各行の左端に出す小さなジャケット。TrackPath に曲ファイルのパスを渡すと、
    /// ArtworkCache（縮小 JPEG のディスクキャッシュ）から非同期に読み込んで表示する。
    /// <para>
    /// ListView は画面外に出た行の部品を別の曲の行として使い回す（仮想化）。
    /// 読み込みを待っている間に別の曲へ切り替わることがあるので、読み終えた時点のパスを確かめてから表示する。
    /// </para>
    /// </summary>
    public sealed partial class TrackArt : Grid
    {
        public static readonly DependencyProperty TrackPathProperty = DependencyProperty.Register(
            nameof(TrackPath), typeof(string), typeof(TrackArt),
            new PropertyMetadata(null, (d, _) => ((TrackArt)d).OnTrackPathChanged()));

        /// <summary>表示する曲のファイルパス。</summary>
        public string? TrackPath
        {
            get => (string?)GetValue(TrackPathProperty);
            set => SetValue(TrackPathProperty, value);
        }

        // ───────── メモリキャッシュ（全 TrackArt で共有） ─────────
        // 同じアルバムの曲は同じ画像なので、フォルダキーごとに BitmapImage を 1 つだけ作って使い回す。
        // 上限を超えたら古い（最近使っていない）ものから捨てる＝LRU 方式。

        private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> Memory = new();
        private static readonly LinkedList<(string Key, BitmapImage Image)> Recent = new();

        /// <summary>画像が無いと分かったキー（行のたびにディスクを確認し直さないため）。</summary>
        private static readonly HashSet<string> NoArt = new();

        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };

        public TrackArt()
        {
            double size = AppConfig.TrackRowArtSize;
            Width = Height = size;
            VerticalAlignment = VerticalAlignment.Center;

            // 下: 画像が無いときのプレースホルダー（音符アイコン）、上: ジャケット画像
            var placeholder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                Child = new FontIcon { Glyph = "", FontSize = size * 0.4, Opacity = 0.5 }
            };
            Children.Add(placeholder);
            Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = _image });
        }

        /// <summary>今表示している画像のフォルダキー（同じアルバムの曲へ使い回されたときに何もしないため）。</summary>
        private string? _shownKey;

        private async void OnTrackPathChanged()
        {
            string? path = TrackPath;
            if (string.IsNullOrEmpty(path))
            {
                _image.Source = null;
                _shownKey = null;
                return;
            }

            string key = ArtworkCache.KeyFor(path);
            // 同じアルバム（同じフォルダ）の曲なら画像も同じなので、何もしない（並べ替え中のちらつき・無駄な処理を防ぐ）
            if (key == _shownKey && _image.Source != null)
                return;

            // メモリにあれば、消さずにそのまま差し替える（一瞬プレースホルダーが見えるちらつきを防ぐ）
            if (TryGetMemory(key, out var cached))
            {
                _image.Source = cached;
                _shownKey = key;
                return;
            }

            // 読み込みが必要なときだけ消す（使い回された行に前の曲の画像が残らないように）
            _image.Source = null;
            _shownKey = null;
            if (NoArt.Contains(key))
                return;

            try
            {
                string? file = await ArtworkCache.GetOrCreateAsync(key, path);
                // 待っている間に別の曲の行として使い回されていたら、この結果は使わない
                if (!string.Equals(TrackPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                if (file == null)
                {
                    NoArt.Add(key);
                    return;
                }

                // DecodePixelWidth: 表示サイズの 2 倍で縮小デコードする（高 DPI でもぼやけず、メモリも小さい）
                var image = new BitmapImage { DecodePixelWidth = (int)(AppConfig.TrackRowArtSize * 2), UriSource = new Uri(file) };
                AddMemory(key, image);
                _image.Source = image;
                _shownKey = key;
            }
            catch (Exception ex)
            {
                // 1 行のジャケットが読めなくても一覧は表示し続ける（async void なので例外を外へ出さない）
                AppRuntime.AppendLifetimeLog($"track-art-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// タグ編集でジャケットを変えたときに呼ぶ。メモリ上の古い画像を捨て、次の表示で読み直させる。
        /// </summary>
        public static void Invalidate(string key)
        {
            NoArt.Remove(key);
            if (Memory.Remove(key, out var node))
                Recent.Remove(node);
        }

        private static bool TryGetMemory(string key, out BitmapImage? image)
        {
            if (Memory.TryGetValue(key, out var node))
            {
                // 使ったものを先頭へ（最近使った順を保つ）
                Recent.Remove(node);
                Recent.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
            image = null;
            return false;
        }

        private static void AddMemory(string key, BitmapImage image)
        {
            if (Memory.ContainsKey(key))
                return;

            var node = Recent.AddFirst((key, image));
            Memory[key] = node;
            while (Recent.Count > AppConfig.TrackRowArtMemoryCacheCount && Recent.Last != null)
            {
                Memory.Remove(Recent.Last.Value.Key);
                Recent.RemoveLast();
            }
        }
    }
}
