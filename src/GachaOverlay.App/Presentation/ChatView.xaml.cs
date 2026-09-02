using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GachaOverlay.App.Presentation;

public partial class ChatView : System.Windows.Controls.UserControl
{
    private ChatViewModel? _viewModel;
    private bool _scrollPending;
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
        }

        _viewModel = viewModel;
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
        if (_scrollPending || _unloaded)
        {
            return;
        }

        _scrollPending = true;
        _scrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollOperation = null;
            _scrollPending = false;
            MessageScroller.ScrollToEnd();
        });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) =>
        AvailableSizeChanged?.Invoke(args.NewSize);
}
