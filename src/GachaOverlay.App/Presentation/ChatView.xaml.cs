using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GachaOverlay.App.Presentation;

public partial class ChatView : System.Windows.Controls.UserControl
{
    private ChatViewModel? _viewModel;
    private bool _scrollPending;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public event Action<System.Windows.Size>? AvailableSizeChanged;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLatestRequested -= RequestScrollToEnd;
            _viewModel.MentionPulseRequested -= RequestMentionPulse;
        }

        _viewModel = args.NewValue as ChatViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToLatestRequested += RequestScrollToEnd;
            _viewModel.MentionPulseRequested += RequestMentionPulse;
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
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollPending = false;
            MessageScroller.ScrollToEnd();
        });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        AvailableSizeChanged?.Invoke(args.NewSize);
}
