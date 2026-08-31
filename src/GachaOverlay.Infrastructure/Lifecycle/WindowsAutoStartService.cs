using Microsoft.Win32;
using System.Runtime.Versioning;

namespace GachaOverlay.Infrastructure.Lifecycle;

public interface IWindowsAutoStartStore
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

[SupportedOSPlatform("windows")]
public sealed class RegistryWindowsAutoStartStore : IWindowsAutoStartStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Write(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStartService
{
    public const string RegistrationName = "GachaOverlay";
    private readonly IWindowsAutoStartStore _store;
    private readonly Func<string?> _processPathProvider;

    public WindowsAutoStartService(
        IWindowsAutoStartStore? store = null,
        Func<string?>? processPathProvider = null)
    {
        _store = store ?? new RegistryWindowsAutoStartStore();
        _processPathProvider = processPathProvider ?? (() => Environment.ProcessPath);
    }

    public bool Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                _store.Delete(RegistrationName);
                return true;
            }

            var processPath = _processPathProvider();
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            _store.Write(RegistrationName, Quote(processPath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsCurrentRegistration()
    {
        var processPath = _processPathProvider();
        return !string.IsNullOrWhiteSpace(processPath) && string.Equals(
            _store.Read(RegistrationName),
            Quote(processPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string path) => $"\"{path}\"";
}
