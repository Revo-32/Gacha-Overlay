using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Chat;

public static partial class ChatMediaSourcePolicy
{
    private static readonly char[] TrailingPunctuation =
        { '.', ',', '!', '?', ';', ':', ')', ']', '}' };

    public static string SuppressExactSourceToken(
        string content,
        ChatMediaCandidate media,
        bool previewSucceeded,
        bool enabled)
    {
        if (!enabled || !previewSucceeded || string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var source = string.IsNullOrWhiteSpace(media.SourceUrl) ? media.Url : media.SourceUrl;
        if (!AreRelated(source!, media.Url))
        {
            return content;
        }

        return UrlTokenPattern().Replace(content, match =>
        {
            var raw = match.Value;
            var candidate = raw.TrimEnd(TrailingPunctuation);
            if (!UrlEquals(candidate, source!))
            {
                return raw;
            }

            return raw[candidate.Length..];
        }).Trim();
    }

    public static bool AreRelated(string sourceUrl, string assetUrl)
    {
        if (!TryNormalize(sourceUrl, out var source) || !TryNormalize(assetUrl, out var asset))
        {
            return false;
        }

        if (UrlEquals(sourceUrl, assetUrl) ||
            string.Equals(source.Host, asset.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourceProvider = ResolveProvider(source.Host);
        return sourceProvider != MediaProvider.Unknown &&
            sourceProvider == ResolveProvider(asset.Host);
    }

    private static bool UrlEquals(string left, string right) =>
        TryNormalize(left, out var leftUri) &&
        TryNormalize(right, out var rightUri) &&
        string.Equals(
            leftUri.AbsoluteUri.TrimEnd('/'),
            rightUri.AbsoluteUri.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalize(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
        uri.Scheme == Uri.UriSchemeHttps;

    private static MediaProvider ResolveProvider(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized is "cdn.discordapp.com" or "media.discordapp.net" or
            "images-ext-1.discordapp.net" or "images-ext-2.discordapp.net")
        {
            return MediaProvider.Discord;
        }

        if (normalized == "tenor.com" || normalized.EndsWith(".tenor.com", StringComparison.Ordinal))
        {
            return MediaProvider.Tenor;
        }

        if (normalized == "klipy.com" || normalized.EndsWith(".klipy.com", StringComparison.Ordinal))
        {
            return MediaProvider.Klipy;
        }

        return MediaProvider.Unknown;
    }

    [GeneratedRegex("https://\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlTokenPattern();

    private enum MediaProvider
    {
        Unknown,
        Discord,
        Tenor,
        Klipy,
    }
}
