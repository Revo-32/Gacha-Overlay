namespace GachaOverlay.Core.Hud.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly IReadOnlyDictionary<string, int> NamedKeys =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
        };

    public bool IsValid =>
        (Modifiers & ~(HotkeyModifiers.Alt |
                       HotkeyModifiers.Control |
                       HotkeyModifiers.Shift |
                       HotkeyModifiers.Windows)) == 0 &&
        IsSupportedVirtualKey(VirtualKey);

    public HotkeySetting ToSetting() => new()
    {
        Modifiers = FormatModifiers(Modifiers),
        Key = FormatVirtualKey(VirtualKey),
    };

    public override string ToString()
    {
        var modifiers = FormatModifiers(Modifiers);
        return string.IsNullOrEmpty(modifiers)
            ? FormatVirtualKey(VirtualKey)
            : $"{modifiers}+{FormatVirtualKey(VirtualKey)}";
    }

    public static bool TryParse(HotkeySetting? setting, out HotkeyGesture gesture)
    {
        gesture = default;
        if (setting is null ||
            !TryParseModifiers(setting.Modifiers, out var modifiers) ||
            !TryParseVirtualKey(setting.Key, out var virtualKey))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return gesture.IsValid;
    }

    public static bool TryParseDisplayText(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        return TryParse(
            new HotkeySetting
            {
                Modifiers = parts.Length == 1 ? string.Empty : string.Join('+', parts[..^1]),
                Key = parts[^1],
            },
            out gesture);
    }

    private static bool TryParseModifiers(string? value, out HotkeyModifiers modifiers)
    {
        modifiers = HotkeyModifiers.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var tokens = value.Split(
            new[] { '+', ',', '|' },
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var parsed = token.ToUpperInvariant() switch
            {
                "ALT" => HotkeyModifiers.Alt,
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (parsed == HotkeyModifiers.None)
            {
                return false;
            }

            modifiers |= parsed;
        }

        return true;
    }

    private static bool TryParseVirtualKey(string? value, out int virtualKey)
    {
        virtualKey = 0;
        var key = value?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Length == 1)
        {
            var character = char.ToUpperInvariant(key[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        if (key.Length is >= 2 and <= 3 &&
            key[0] is 'F' or 'f' &&
            int.TryParse(key[1..], out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = 0x70 + functionKey - 1;
            return true;
        }

        return NamedKeys.TryGetValue(key, out virtualKey);
    }

    private static bool IsSupportedVirtualKey(int virtualKey) =>
        virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 0x70 and <= 0x87 ||
        NamedKeys.Values.Contains(virtualKey);

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var values = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            values.Add("Control");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            values.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            values.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            values.Add("Windows");
        }

        return string.Join('+', values);
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        return NamedKeys.FirstOrDefault(pair => pair.Value == virtualKey).Key ?? "Unknown";
    }
}
