using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;

namespace GachaOverlay.App.Presentation;

internal sealed class ChatViewModel : INotifyPropertyChanged
{
    public GachaOverlay.Core.Chat.ChatScrollState ScrollState { get; } = new();
    public event Action? BeforeMessagesChanged;
    public event Action? AfterMessagesChanged;
    public event Action<string>? ChannelFeedbackRequested;
    public void BeginMessageUpdate() => BeforeMessagesChanged?.Invoke();
    public void EndMessageUpdate() => AfterMessagesChanged?.Invoke();
    public void NotifyNewMessage() { ScrollState.ReceiveNewMessage(); NotifyScrollState(); }
    public void ObserveUserScroll(double offset, double height) { ScrollState.ObserveUserOffset(offset, height); NotifyScrollState(); }
    public void JumpToLatest() { ScrollState.FollowLatest(); NotifyScrollState(); ScrollToLatestRequested?.Invoke(); }
    public void NotifyCommittedChannel(string label) { JumpToLatest(); ChannelFeedbackRequested?.Invoke("#" + label.TrimStart('#')); }
    public bool IsJumpVisible => IsHudUnlocked && !ScrollState.IsFollowingLatest && ScrollState.UnreadCount > 0;
    public string UnreadLabel => "↓ " + ScrollState.UnreadCount;
    private void NotifyScrollState() { OnPropertyChanged(nameof(IsJumpVisible)); OnPropertyChanged(nameof(UnreadLabel)); }
    private ImageSource? _previewImage;
    private bool _isHudUnlocked;
    private string _connectionText = string.Empty;
    private Thickness _paintViewportPadding;

    public ChatViewModel()
    {
        ClosePreviewCommand = new RelayCommand(() => PreviewImage = null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? ScrollToLatestRequested;

    public event Action? MentionPulseRequested;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public bool IsHudUnlocked
    {
        get => _isHudUnlocked;
        set { if (_isHudUnlocked == value) return; _isHudUnlocked = value; OnPropertyChanged(); NotifyScrollState(); }
    }

    public ICommand ClosePreviewCommand { get; }

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (ReferenceEquals(_previewImage, value))
            {
                return;
            }

            _previewImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPreviewOpen));
        }
    }

    public bool IsPreviewOpen => PreviewImage is not null;

    public string ConnectionText
    {
        get => _connectionText;
        set
        {
            if (string.Equals(_connectionText, value, StringComparison.Ordinal))
            {
                return;
            }

            _connectionText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHealthVisible));
        }
    }

    public bool IsHealthVisible => !string.IsNullOrWhiteSpace(ConnectionText);

    public Thickness PaintViewportPadding
    {
        get => _paintViewportPadding;
        set
        {
            if (_paintViewportPadding == value)
            {
                return;
            }

            _paintViewportPadding = value;
            OnPropertyChanged();
        }
    }

    public void RequestScrollToLatest() { if (ScrollState.IsFollowingLatest) ScrollToLatestRequested?.Invoke(); }

    public void RequestMentionPulse() => MentionPulseRequested?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
