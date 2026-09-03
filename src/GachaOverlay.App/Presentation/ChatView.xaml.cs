using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GachaOverlay.App.Presentation;

public partial class ChatView : System.Windows.Controls.UserControl
{
    private ChatViewModel? _viewModel;
    private bool _scrollPending;
    private string? _anchorId;
    private double _anchorTop;
    private double _oldOffset;
    private bool _restoringAnchor;
    private bool _unloaded;
    private DispatcherOperation? _scrollOperation;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            _unloaded = false;
            BindViewModel(DataContext as ChatViewModel);
        };
        Unloaded += (_, _) =>
        {
            _unloaded = true;
            BindViewModel(null);
            _scrollOperation?.Abort();
            _scrollOperation = null;
            _scrollPending = false;
            MentionNotificationBorder.BeginAnimation(OpacityProperty, null);
            ChannelFeedback.BeginAnimation(OpacityProperty, null);
            _anchorId = null;
        };
    }

    public event Action<System.Windows.Size>? AvailableSizeChanged;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
        => BindViewModel(_unloaded ? null : args.NewValue as ChatViewModel);

    private void BindViewModel(ChatViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLatestRequested -= RequestScrollToEnd;
            _viewModel.MentionPulseRequested -= RequestMentionPulse;
            _viewModel.BeforeMessagesChanged -= CaptureAnchor;
            _viewModel.AfterMessagesChanged -= RestoreAnchor;
            _viewModel.ChannelFeedbackRequested -= ShowChannelFeedback;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLatestRequested += RequestScrollToEnd;
            _viewModel.MentionPulseRequested += RequestMentionPulse;
            _viewModel.BeforeMessagesChanged += CaptureAnchor;
            _viewModel.AfterMessagesChanged += RestoreAnchor;
            _viewModel.ChannelFeedbackRequested += ShowChannelFeedback;
        }
    }

    private void RequestMentionPulse()
    {
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(440),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        MentionNotificationBorder.BeginAnimation(
            OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void RequestScrollToEnd()
    {
        if (_scrollPending || _unloaded)
        {
            return;
        }

        _scrollPending = true;
        _scrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollOperation = null;
            _scrollPending = false;
            if (_viewModel?.ScrollState.IsFollowingLatest == true) MessageScroller.ScrollToEnd();
        });
    }

    private void OnChatMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (_viewModel?.IsHudUnlocked != true) return;
        _scrollOperation?.Abort();
        _scrollOperation = null;
        _scrollPending = false;
        _anchorId = null;
        var target = Math.Clamp(MessageScroller.VerticalOffset - args.Delta, 0, MessageScroller.ScrollableHeight);
        // Set semantic state before layout or a previously queued auto-follow can run.
        _viewModel.ObserveUserScroll(target, MessageScroller.ScrollableHeight);
        MessageScroller.ScrollToVerticalOffset(target);
        args.Handled = true;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        // Extent changes (new messages/media/eviction) are not user scroll intent.
        if (!_restoringAnchor && !_scrollPending && args.ExtentHeightChange == 0 &&
            args.ViewportHeightChange == 0 && args.VerticalChange != 0 && _viewModel?.IsHudUnlocked == true)
            _viewModel.ObserveUserScroll(MessageScroller.VerticalOffset, MessageScroller.ScrollableHeight);
    }

    private void OnJumpToLatest(object sender, RoutedEventArgs args) => _viewModel?.JumpToLatest();

    private void ShowChannelFeedback(string label)
    {
        if (_unloaded) return;
        ChannelFeedbackText.Text = label;
        ChannelFeedback.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = {
                new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.3))),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))) },
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void CaptureAnchor()
    {
        if (_viewModel?.ScrollState.IsFollowingLatest != false || _unloaded) return;
        _anchorId = null;
        _oldOffset = MessageScroller.VerticalOffset;
        foreach (var item in _viewModel.Messages)
        {
            if (MessageItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement element) continue;
            var top = element.TranslatePoint(new System.Windows.Point(), MessageScroller).Y;
            if (top + element.ActualHeight <= 0) continue;
            _anchorId = item.MessageId;
            _anchorTop = top;
            break;
        }
    }

    private void RestoreAnchor()
    {
        if (_viewModel?.ScrollState.IsFollowingLatest != false || _unloaded) return;
        _scrollOperation?.Abort();
        _scrollPending = true;
        _scrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollOperation = null;
            try
            {
                if (_viewModel?.ScrollState.IsFollowingLatest != false || _unloaded) return;
                _restoringAnchor = true;
                var item = _viewModel.Messages.FirstOrDefault(message => message.MessageId == _anchorId);
                var target = _oldOffset;
                if (item is not null && MessageItems.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement element)
                    target = MessageScroller.VerticalOffset + element.TranslatePoint(new System.Windows.Point(), MessageScroller).Y - _anchorTop;
                MessageScroller.ScrollToVerticalOffset(Math.Clamp(target, 0, MessageScroller.ScrollableHeight));
                MessageScroller.UpdateLayout();
            }
            finally { _anchorId = null; _restoringAnchor = false; _scrollPending = false; }
        });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        AvailableSizeChanged?.Invoke(args.NewSize);
}
