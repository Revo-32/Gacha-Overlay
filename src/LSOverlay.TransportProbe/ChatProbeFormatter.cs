using System.Text;
using LSOverlay.Protocol;

namespace LSOverlay.TransportProbe;

internal static class ChatProbeFormatter
{
    internal const int MaximumForwardSnapshots = 4;
    internal const int MaximumForwardAttachments = 4;
    internal const int MaximumForwardTextLength = 240;

    public static string Format(string operation, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(message);
        var output = new StringBuilder();
        var content = SafeText(message.Content, 80, "<no text>");
        var author = SafeText(
            message.Author?.DisplayName ?? message.Author?.Username,
            80,
            "snapshot");
        output.Append(SafeText(operation, 80, "chat"));
        output.Append(": id=");
        output.Append(message.MessageId);
        output.Append(" author=");
        output.Append(author);
        output.Append(" text=\"");
        output.Append(content);
        output.Append("\" emoji=");
        output.Append(message.CustomEmojis.Count);
        output.Append(" attachments=");
        output.Append(message.Attachments.Count);
        output.Append(" voice=");
        output.Append(message.Attachments.Count(item => item.IsVoiceMessage));
        output.Append(" embeds=");
        output.Append(message.Embeds.Count);
        output.Append(" stickers=");
        output.Append(message.Stickers.Count);
        output.Append(" forwards=");
        output.Append(message.ForwardedSnapshots.Count);
        output.Append(" reply=");
        output.Append(message.Reference is null ? 0 : 1);
        output.Append(" components=");
        output.Append(message.Components.Count);
        output.Append(" poll=");
        output.Append(message.Poll is null ? 0 : 1);

        for (var index = 0;
             index < Math.Min(message.ForwardedSnapshots.Count, MaximumForwardSnapshots);
             index++)
        {
            AppendForward(output, index + 1, message.ForwardedSnapshots[index]);
        }

        if (message.ForwardedSnapshots.Count > MaximumForwardSnapshots)
        {
            output.AppendLine();
            output.Append("  Forward: ");
            output.Append(message.ForwardedSnapshots.Count - MaximumForwardSnapshots);
            output.Append(" additional snapshot(s) omitted");
        }

        return output.ToString();
    }

    private static void AppendForward(
        StringBuilder output,
        int index,
        ChatForwardSnapshot forward)
    {
        output.AppendLine();
        output.Append("  Forward[");
        output.Append(index);
        output.AppendLine("]:");
        output.Append("    text=\"");
        output.Append(SafeText(
            forward.Content,
            MaximumForwardTextLength,
            "<no text>"));
        output.AppendLine("\"");
        output.Append("    attachments=");
        output.Append(forward.Attachments.Count);
        output.Append(" embeds=");
        output.Append(forward.Embeds.Count);
        output.Append(" stickers=");
        output.Append(forward.Stickers.Count);
        output.Append(" components=");
        output.AppendLine(forward.Components.Count.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        output.AppendLine("    originalAuthor=<unavailable>");

        for (var attachmentIndex = 0;
             attachmentIndex < Math.Min(
                 forward.Attachments.Count,
                 MaximumForwardAttachments);
             attachmentIndex++)
        {
            var attachment = forward.Attachments[attachmentIndex];
            output.Append("    attachment[");
            output.Append(attachmentIndex + 1);
            output.Append("]: file=\"");
            output.Append(SafeText(attachment.FileName, 120, "<unnamed>"));
            output.Append("\" type=\"");
            output.Append(SafeText(attachment.ContentType, 80, "unknown"));
            output.Append("\" size=");
            output.Append(attachment.Size);
            output.Append(" dimensions=");
            output.Append(attachment.Width is { } width && attachment.Height is { } height
                ? $"{width}x{height}"
                : "unknown");
            output.AppendLine();
        }

        if (forward.Attachments.Count > MaximumForwardAttachments)
        {
            output.Append("    attachment: ");
            output.Append(forward.Attachments.Count - MaximumForwardAttachments);
            output.AppendLine(" additional item(s) omitted");
        }
    }

    private static string SafeText(string? value, int maximumLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sanitized = new string(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Replace('"', '\'')
            .Trim();
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength] + "...";
    }
}
