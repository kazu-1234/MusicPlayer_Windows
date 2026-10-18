using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MusicPlayer.Views
{
    /// <summary>
    /// 一覧（プレイリスト・再生キュー）の「元に戻す／やり直し」。
    /// 変更の直前に一覧全体のコピー（スナップショット）を積んでおき、戻すときはそのコピーで一覧を置き換える。
    /// 1 回の変更で並べ替え・削除・追加のどれが起きても同じ仕組みで戻せる（数百曲程度ならコピーは軽い）。
    /// </summary>
    public sealed class UndoStack<T>
    {
        private readonly List<List<T>> _undo = new();
        private readonly List<List<T>> _redo = new();

        /// <summary>変更する直前の一覧を記録する。新しい変更をしたら「やり直し」の履歴は捨てる（一般的なアプリと同じ）。</summary>
        public void Push(IEnumerable<T> before)
        {
            _undo.Add(before.ToList());
            // 履歴が増えすぎないよう、古いものから捨てる
            if (_undo.Count > AppConfig.UndoHistoryLimit)
                _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>1 つ前の状態を返す（無ければ null）。今の状態は「やり直し」用に積む。</summary>
        public List<T>? Undo(IEnumerable<T> current) => Move(_undo, _redo, current);

        /// <summary>元に戻した変更をやり直した状態を返す（無ければ null）。</summary>
        public List<T>? Redo(IEnumerable<T> current) => Move(_redo, _undo, current);

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        private static List<T>? Move(List<List<T>> from, List<List<T>> to, IEnumerable<T> current)
        {
            if (from.Count == 0)
                return null;

            var state = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(current.ToList());
            return state;
        }
    }

    /// <summary>Ctrl+Z（元に戻す）／Ctrl+Y・Ctrl+Shift+Z（やり直し）のショートカットをページに付ける。</summary>
    public static class UndoKeys
    {
        public static void Attach(UIElement scope, Action undo, Action redo)
        {
            Add(VirtualKey.Z, VirtualKeyModifiers.Control, undo);
            Add(VirtualKey.Y, VirtualKeyModifiers.Control, redo);
            Add(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, redo);
            // ボタン等にショートカットのツールチップを出さない
            scope.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
            {
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
                accelerator.Invoked += (_, args) =>
                {
                    // 文字入力中は、入力欄自身の「元に戻す」（打った文字の取り消し）を優先する
                    if (IsTextInputFocused(scope))
                        return;
                    action();
                    args.Handled = true;
                };
                scope.KeyboardAccelerators.Add(accelerator);
            }
        }

        private static bool IsTextInputFocused(UIElement scope) =>
            scope.XamlRoot != null
            && FocusManager.GetFocusedElement(scope.XamlRoot) is TextBox or AutoSuggestBox or PasswordBox or RichEditBox;
    }
}
