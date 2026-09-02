namespace GachaOverlay.Core.Discord.Messages;

public sealed record DiscordAttachmentMetadata(
    string AttachmentId,
    string? FileName,
    string? Url,
    string? ProxyUrl,
    long? Size,
    int? Width,
    int? Height,
    string? ContentType = null,
    string? Description = null,
    string? Title = null,
    bool IsEphemeral = false,
    double? DurationSeconds = null,
    string? WaveformBase64 = null,
    bool IsVoiceMessage = false);
