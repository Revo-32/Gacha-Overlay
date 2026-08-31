using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;

namespace GachaOverlay.App.Presentation;

internal sealed class ChatViewModel : INotifyPropertyChanged
{
    private ImageSource? _previewImage;
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
        }
    }

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

    public void RequestScrollToLatest() => ScrollToLatestRequested?.Invoke();

    public void RequestMentionPulse() => MentionPulseRequested?.Invoke();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
