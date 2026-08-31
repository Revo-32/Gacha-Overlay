using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Logging;
using FontFamily = System.Windows.Media.FontFamily;

namespace GachaOverlay.App.Presentation;

internal interface IChatFontCatalog
{
    bool TryResolveBundled(
        string wpfFamilyName,
        string metadataFamilyName,
        FontWeight requestedWeight,
        string resolvedDisplayName,
        out ResolvedChatFontRole? role,
        out ChatFontFallbackReason failureReason);

    bool TryResolveSystem(
        string familyName,
        FontWeight requestedWeight,
        out ResolvedChatFontRole? role);

    ResolvedChatFontRole ResolveFallback(
        FontWeight requestedWeight,
        ChatFontFallbackReason reason);
}

internal sealed class ChatTypographyResolver
{
    private readonly IChatFontCatalog _catalog;
    private readonly IAppLogger _logger;
    private readonly Dictionary<ChatFontPreset, ResolvedChatTypography> _cache = new();

    public ChatTypographyResolver(IAppLogger logger, IChatFontCatalog? catalog = null)
    {
        _logger = logger;
        _catalog = catalog ?? new WpfChatFontCatalog();
    }

    public ResolvedChatTypography Resolve(ChatFontPreset requested)
    {
        var normalized = requested == ChatFontPreset.KoPubWorldDotum
            ? ChatFontPreset.WantedSans
            : Enum.IsDefined(requested)
                ? requested
                : ChatFontPreset.Kimm;
        if (_cache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var resolved = normalized switch
        {
            ChatFontPreset.Pretendard => ResolvePretendard(),
            ChatFontPreset.Cafe24ProSlim => ResolveCafe24(),
            ChatFontPreset.WantedSans => ResolveWantedSans(),
            _ => ResolveKimm(),
        };
        _cache.Add(normalized, resolved);
        Log(resolved, "Nickname", resolved.Nickname);
        Log(resolved, "Message", resolved.Message);
        return resolved;
    }

    private ResolvedChatTypography ResolveKimm()
    {
        var definition = ChatSettings.ResolveTypography(ChatFontPreset.Kimm);
        var nickname = ResolveBundled(
            "KIMM",
            definition.NicknameFamilyName,
            FontWeights.Bold,
            "KIMM Bold");
        var message = ResolveBundled(
            "KIMM L",
            definition.MessageFamilyName,
            FontWeights.Light,
            "KIMM Light");
        return new ResolvedChatTypography(
            ChatFontPreset.Kimm,
            definition.DisplayName,
            nickname,
            message);
    }

    private ResolvedChatTypography ResolveCafe24()
    {
        var definition = ChatSettings.ResolveTypography(ChatFontPreset.Cafe24ProSlim);
        var nickname = ResolveBundled(
            "Cafe24 PRO Slim Max",
            definition.NicknameFamilyName,
            FontWeights.Bold,
            "Cafe24 PRO Slim Max");
        var message = ResolveBundled(
            "Cafe24 PRO Slim Fit",
            definition.MessageFamilyName,
            FontWeights.Normal,
            "Cafe24 PRO Slim Fit");
        return new ResolvedChatTypography(
            ChatFontPreset.Cafe24ProSlim,
            definition.DisplayName,
            nickname,
            message);
    }

    private ResolvedChatTypography ResolvePretendard()
    {
        var definition = ChatSettings.ResolveTypography(ChatFontPreset.Pretendard);
        return new ResolvedChatTypography(
            ChatFontPreset.Pretendard,
            definition.DisplayName,
            ResolveBundled("Pretendard Variable", "Pretendard Variable", FontWeights.SemiBold, "Pretendard SemiBold"),
            ResolveBundled("Pretendard Variable", "Pretendard Variable", FontWeights.Normal, "Pretendard Regular"));
    }

    private ResolvedChatTypography ResolveWantedSans()
    {
        var definition = ChatSettings.ResolveTypography(ChatFontPreset.WantedSans);
        return new ResolvedChatTypography(
            ChatFontPreset.WantedSans,
            definition.DisplayName,
            ResolveBundled("Wanted Sans Variable", "Wanted Sans Variable", FontWeights.Bold, "Wanted Sans Bold"),
            ResolveBundled("Wanted Sans Variable", "Wanted Sans Variable", FontWeights.Medium, "Wanted Sans Medium"));
    }

    private ResolvedChatFontRole ResolveBundled(
        string wpfFamilyName,
        string metadataFamilyName,
        FontWeight weight,
        string resolvedDisplayName)
    {
        if (_catalog.TryResolveBundled(
                wpfFamilyName,
                metadataFamilyName,
                weight,
                resolvedDisplayName,
                out var role,
                out var failureReason))
        {
            return role!;
        }

        return _catalog.ResolveFallback(weight, failureReason);
    }

    private void Log(
        ResolvedChatTypography typography,
        string roleName,
        ResolvedChatFontRole role)
    {
        _logger.Information(
            "FONT",
            $"Style={ResolveStyleName(typography.RequestedFont)} role={roleName} " +
            $"requested=\"{typography.RequestedDisplayName}\" " +
            $"resolved=\"{role.ResolvedDisplayName}\" weight={role.FontWeight} " +
            $"source={role.Source} fallback={role.IsFallback.ToString().ToLowerInvariant()} " +
            $"reason={role.FallbackReason?.ToString() ?? "none"}.");
    }

    private static string ResolveStyleName(ChatFontPreset preset) => preset switch
    {
        ChatFontPreset.Pretendard => "Clean",
        ChatFontPreset.WantedSans => "HighReadability",
        ChatFontPreset.Cafe24ProSlim => "GtaLegacy",
        _ => "Modern",
    };
}

internal sealed class WpfChatFontCatalog : IChatFontCatalog
{
    private static readonly Uri BundledFontResourceLocation =
        CreateBundledFontResourceLocation();

    private static readonly string[] FallbackFamilies =
    {
        "Malgun Gothic",
        "Yu Gothic UI",
        "Meiryo UI",
        "Segoe UI",
    };

    private readonly string? _bundledFontDirectory;
    private readonly Uri _bundledFontLocation;
    private IReadOnlyList<FontFamily>? _bundledFamilies;
    private IReadOnlyList<FontFamily>? _systemFamilies;

    public WpfChatFontCatalog(string? bundledFontDirectory = null)
    {
        _bundledFontDirectory = bundledFontDirectory;
        _bundledFontLocation = bundledFontDirectory is null
            ? BundledFontResourceLocation
            : CreateDirectoryUri(bundledFontDirectory);
    }

    private static Uri CreateBundledFontResourceLocation()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(System.IO.Packaging.PackUriHelper).TypeHandle);
        return new Uri(
            "pack://application:,,,/GachaOverlay.App;component/Assets/Fonts/",
            UriKind.Absolute);
    }

    public bool TryResolveBundled(
        string wpfFamilyName,
        string metadataFamilyName,
        FontWeight requestedWeight,
        string resolvedDisplayName,
        out ResolvedChatFontRole? role,
        out ChatFontFallbackReason failureReason)
    {
        role = null;
        if (_bundledFontDirectory is not null &&
            !Directory.Exists(_bundledFontDirectory))
        {
            failureReason = ChatFontFallbackReason.BundledDirectoryMissing;
            return false;
        }

        var family = GetBundledFamilies().FirstOrDefault(candidate =>
            HasFamilyName(candidate, wpfFamilyName));
        if (family is null)
        {
            failureReason = ChatFontFallbackReason.BundledFamilyNotFound;
            return false;
        }

        var typeface = FindTypeface(family, requestedWeight, metadataFamilyName);
        if (typeface is null)
        {
            failureReason = family.GetTypefaces().Any(candidate =>
                candidate.TryGetGlyphTypeface(out _))
                    ? ChatFontFallbackReason.FamilyMetadataMismatch
                    : ChatFontFallbackReason.TypefaceUnavailable;
            return false;
        }

        typeface.TryGetGlyphTypeface(out var glyphTypeface);
        role = new ResolvedChatFontRole(
            family,
            glyphTypeface!.Weight,
            resolvedDisplayName,
            ChatFontResolutionSource.Bundled,
            IsFallback: false,
            FallbackReason: null);
        failureReason = default;
        return true;
    }

    public bool TryResolveSystem(
        string familyName,
        FontWeight requestedWeight,
        out ResolvedChatFontRole? role)
    {
        role = null;
        var family = GetSystemFamilies().FirstOrDefault(candidate =>
            HasFamilyName(candidate, familyName));
        if (family is null)
        {
            return false;
        }

        var typeface = FindTypeface(family, requestedWeight, expectedMetadataFamilyName: null);
        if (typeface is null || !typeface.TryGetGlyphTypeface(out var glyphTypeface))
        {
            return false;
        }

        role = new ResolvedChatFontRole(
            family,
            glyphTypeface.Weight,
            ResolveFamilyName(glyphTypeface, familyName),
            ChatFontResolutionSource.System,
            IsFallback: false,
            FallbackReason: null);
        return true;
    }

    public ResolvedChatFontRole ResolveFallback(
        FontWeight requestedWeight,
        ChatFontFallbackReason reason)
    {
        foreach (var fallback in FallbackFamilies)
        {
            if (TryResolveSystem(fallback, requestedWeight, out var resolved))
            {
                return resolved! with
                {
                    Source = ChatFontResolutionSource.Fallback,
                    IsFallback = true,
                    FallbackReason = reason,
                };
            }
        }

        return new ResolvedChatFontRole(
            new FontFamily("Segoe UI"),
            requestedWeight,
            "Segoe UI",
            ChatFontResolutionSource.Fallback,
            IsFallback: true,
            FallbackReason: ChatFontFallbackReason.SystemFallbackUnavailable);
    }

    private IReadOnlyList<FontFamily> GetBundledFamilies()
    {
        if (_bundledFamilies is not null)
        {
            return _bundledFamilies;
        }

        _bundledFamilies = Fonts.GetFontFamilies(_bundledFontLocation).ToArray();
        return _bundledFamilies;
    }

    private static Uri CreateDirectoryUri(string directory)
    {
        var absolute = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return new Uri(absolute, UriKind.Absolute);
    }

    private IReadOnlyList<FontFamily> GetSystemFamilies() =>
        _systemFamilies ??= Fonts.SystemFontFamilies.ToArray();

    private static Typeface? FindTypeface(
        FontFamily family,
        FontWeight requestedWeight,
        string? expectedMetadataFamilyName) =>
        family.GetTypefaces()
            .Select(candidate => new
            {
                Typeface = candidate,
                HasGlyph = candidate.TryGetGlyphTypeface(out var glyph),
                Glyph = glyph,
            })
            .Where(candidate =>
                candidate.HasGlyph &&
                (expectedMetadataFamilyName is null ||
                 candidate.Glyph!.FamilyNames.Values.Any(name =>
                     string.Equals(
                         name,
                         expectedMetadataFamilyName,
                         StringComparison.OrdinalIgnoreCase))))
            .OrderBy(candidate => Math.Abs(
                candidate.Glyph!.Weight.ToOpenTypeWeight() -
                requestedWeight.ToOpenTypeWeight()))
            .Select(candidate => candidate.Typeface)
            .FirstOrDefault();

    private static bool HasFamilyName(FontFamily family, string expected) =>
        string.Equals(family.Source, expected, StringComparison.OrdinalIgnoreCase) ||
        family.Source.EndsWith($"#{expected}", StringComparison.OrdinalIgnoreCase) ||
        family.FamilyNames.Values.Any(name =>
            string.Equals(name, expected, StringComparison.OrdinalIgnoreCase));

    private static string ResolveFamilyName(GlyphTypeface glyphTypeface, string fallback)
    {
        if (glyphTypeface.FamilyNames.TryGetValue(
                CultureInfo.GetCultureInfo("en-US"),
                out var english))
        {
            return english;
        }

        return glyphTypeface.FamilyNames.Values.FirstOrDefault() ?? fallback;
    }
}
