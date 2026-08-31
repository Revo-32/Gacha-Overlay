using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using GachaOverlay.Core.Chat;

namespace GachaOverlay.App.Presentation;

public sealed class ChatTokenViewModel : INotifyPropertyChanged
{
    private ImageSource? _image;

    public ChatTokenViewModel(ChatToken token)
    {
        Kind = token.Kind;
        Text = token.Text;
        Identity = token.Identity;
        IsSelfMention = token.IsSelfMention;
        IsAnimatedEmoji = token.IsAnimatedEmoji;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatTokenKind Kind { get; }

    public string Text { get; }

    public string? Identity { get; }

    public bool IsSelfMention { get; }

    public bool IsAnimatedEmoji { get; }

    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value))
            {
                return;
            }

            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }
}
