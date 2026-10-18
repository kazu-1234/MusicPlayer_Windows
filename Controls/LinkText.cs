using Microsoft.UI.Input;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MusicPlayer.Controls
{
    /// <summary>
    /// 押せる文字（アーティスト名・アルバム名を押してそのページへ飛ぶのに使う）。
    /// 普段は普通の文字と同じ見た目で、マウスを乗せたときだけ下線と手の形のカーソルを出す（WMP と同じ）。
    /// <para>
    /// Grid を継承しているのは、カーソルを変える ProtectedCursor が UIElement の派生クラスからしか設定できないため。
    /// 押したときは Handled にして、外側のボタン（再生バーの曲情報）や一覧の行の選択・ダブルクリック再生に押下を渡さない。
    /// </para>
    /// </summary>
    public sealed class LinkText : Grid
    {
        private readonly TextBlock _text = new();

        /// <summary>左ボタンを押した状態か（同じ場所で離したときだけ Click にする）。</summary>
        private bool _pressed;

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(LinkText),
            new PropertyMetadata("", (d, e) => ((LinkText)d)._text.Text = e.NewValue as string ?? ""));

        public LinkText()
        {
            // 文字の幅だけを押せる範囲にする（列の右側の空白を押しても飛ばない）
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Center;
            // 背景が null だと文字の隙間でポインターを受け取れないので、透明で塗っておく
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Children.Add(_text);

            PointerEntered += (_, _) =>
            {
                _text.TextDecorations = TextDecorations.Underline;
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            };
            PointerExited += (_, _) => _text.TextDecorations = TextDecorations.None;
            PointerPressed += OnPressed;
            PointerReleased += OnReleased;
            PointerCaptureLost += (_, _) => _pressed = false;
            // 行のダブルクリック再生・タップ選択を起こさない
            Tapped += (_, e) => e.Handled = true;
            DoubleTapped += (_, e) => e.Handled = true;
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>中の文字に当てるスタイル（ListSecondaryTextStyle など、元の TextBlock と同じものを渡す）。</summary>
        public Style? TextStyle
        {
            get => _text.Style;
            set => _text.Style = value;
        }

        /// <summary>文字の大きさ（スタイルより後から個別に変えたいとき用。再生バーは AppConfig の値を入れる）。</summary>
        public double FontSize
        {
            get => _text.FontSize;
            set => _text.FontSize = value;
        }

        /// <summary>文字を押して離した。</summary>
        public event RoutedEventHandler? Click;

        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return; // 右クリックは一覧の右クリックメニューに任せる

            _pressed = true;
            CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed)
                return;

            _pressed = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            // 押したまま外へ動かして離したら取り消し（普通のボタンと同じ）
            Point p = e.GetCurrentPoint(this).Position;
            if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight)
                Click?.Invoke(this, new RoutedEventArgs());
        }
    }
}
