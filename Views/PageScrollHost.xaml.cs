using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MusicPlayer.Views
{
    public sealed partial class PageScrollHost : ContentControl
    {
        private ScrollViewer? _scrollViewer;
        private FrameworkElement? _contentRoot;
        private bool _scrollEnabled;
        private bool _updateScheduled;

        public PageScrollHost()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (_, __) => ScheduleUpdateScrollability();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = GetTemplateChild("PART_ScrollViewer") as ScrollViewer;
            if (_scrollViewer != null)
            {
                _scrollViewer.SizeChanged += (_, __) => ScheduleUpdateScrollability();
                _scrollViewer.PointerWheelChanged += ScrollViewer_PointerWheelChanged;
            }

            WatchContentRoot(Content as FrameworkElement);
            ScheduleUpdateScrollability();
        }

        protected override void OnContentChanged(object oldContent, object newContent)
        {
            base.OnContentChanged(oldContent, newContent);

            if (oldContent is FrameworkElement oldRoot)
                UnwatchContentRoot(oldRoot);

            WatchContentRoot(newContent as FrameworkElement);
            ScheduleUpdateScrollability();
        }

        private void WatchContentRoot(FrameworkElement? root)
        {
            if (root == null)
                return;

            _contentRoot = root;
            root.SizeChanged += ContentRoot_SizeChanged;
            root.Loaded += ContentRoot_Loaded;
        }

        private void UnwatchContentRoot(FrameworkElement root)
        {
            root.SizeChanged -= ContentRoot_SizeChanged;
            root.Loaded -= ContentRoot_Loaded;

            if (ReferenceEquals(_contentRoot, root))
                _contentRoot = null;
        }

        private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
        {
            ScheduleUpdateScrollability();
        }

        private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleUpdateScrollability();
        }

        private void ScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!_scrollEnabled)
                e.Handled = true;
        }

        private void ScheduleUpdateScrollability()
        {
            if (_updateScheduled)
                return;

            _updateScheduled = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _updateScheduled = false;
                UpdateScrollability();
            });
        }

        private void UpdateScrollability()
        {
            if (_scrollViewer == null)
                return;

            _scrollViewer.UpdateLayout();
            _contentRoot?.UpdateLayout();

            bool needsScroll = ComputeNeedsScroll();
            ApplyScrollState(needsScroll);

            if (!needsScroll && _scrollViewer.VerticalOffset > 0)
                _scrollViewer.ChangeView(null, 0, null, disableAnimation: true);

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_scrollViewer == null)
                    return;

                bool needsScrollAfterLayout = ComputeNeedsScroll();
                ApplyScrollState(needsScrollAfterLayout);

                if (!needsScrollAfterLayout && _scrollViewer.VerticalOffset > 0)
                    _scrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            });
        }

        private bool ComputeNeedsScroll()
        {
            if (_scrollViewer == null)
                return false;

            double viewportHeight = _scrollViewer.ViewportHeight;
            if (viewportHeight <= 0)
                return false;

            double contentHeight = _contentRoot?.ActualHeight ?? 0;
            if (contentHeight <= 0)
                contentHeight = _scrollViewer.ExtentHeight;

            return contentHeight > viewportHeight + 0.5;
        }

        private void ApplyScrollState(bool enabled)
        {
            if (_scrollViewer == null)
                return;

            // スクロールの許可・バーの表示は常に Auto のままにする（内容の高さを正しく測るため）。
            // 収まっているときの「空スクロール」だけ、ホイールを止めて防ぐ
            _scrollEnabled = enabled;
        }
    }
}
