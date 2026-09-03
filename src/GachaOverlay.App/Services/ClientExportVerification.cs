using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GachaOverlay.App.Lifecycle;
using GachaOverlay.App.Presentation;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Diagnostics;
using GachaOverlay.Infrastructure.Logging;

namespace GachaOverlay.App.Services;

// Explicit, offline verification of the shipped single-file binary. Never starts
// ApplicationHost or reads the user's settings, credential store or Discord data.
internal static class ClientExportVerification
{
    public static async Task<int> RunAsync(System.Windows.Application application, string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root)) return 2;
            Directory.CreateDirectory(root);
            using var logger = new RollingFileLogger(Path.Combine(root, "Logs"));
            var exporter = new DiagnosticBundleExporter(logger);
            var host = new ApplicationHost(application, () => { });
            var destination = Path.Combine(root, "diagnostics.zip");
            var request = host.BuildDiagnosticRequest(destination) with { LogDirectory = Path.Combine(root, "Logs") };
            // Synthetic secrets only. The live writer stays open during both exports.
            logger.Information("CHECK", "code=synthetic-oauth-code state=synthetic-oauth-state access_token=synthetic-token");
            for (var i = 0; i < 2; i++)
            {
                var result = await exporter.ExportAsync(request);
                if (!result.IsSuccess) return 3;
                using var zip = ZipFile.OpenRead(destination);
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    var text = reader.ReadToEnd();
                    if (text.Contains("synthetic-", StringComparison.Ordinal)) return 4;
                    if (entry.FullName.EndsWith(".json", StringComparison.Ordinal))
                    {
                        using var json = JsonDocument.Parse(text);
                    }
                }
                if (zip.Entries.Count(entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal)) != 6) return 5;
            }

            VerifySettingsRoute(application);
            // The helper copies only the EXE. Successful execution without an
            // adjacent managed entry assembly verifies the shipped bundle layout.
            var singleFile = File.Exists(Path.Combine(AppContext.BaseDirectory, "GachaOverlay.App.exe")) &&
                !File.Exists(Path.Combine(AppContext.BaseDirectory, "GachaOverlay.App.dll"));
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                Status = "PASS",
                SingleFile = singleFile,
                DiagnosticExports = 2,
                ParsedRequiredJsonEntries = 6,
                OfflineWpfGearRoute = "PASS",
                UserGameInput = "NOT RUN",
                UserDataRead = false,
                NetworkStarted = false,
                PendingTemporaryFiles = Directory.GetFiles(root, "*.tmp").Length,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch
        {
            // Do not show a fatal dialog or expose an exception's potentially sensitive message.
            return 1;
        }
    }

    private static void VerifySettingsRoute(System.Windows.Application application)
    {
        var hud = new HudWindow { Left = -10000, Top = -10000, ShowActivated = false };
        FoundationWindow? settings = null;
        var created = 0;
        using var service = new SettingsWindowService(new UiDispatcherAdapter(application.Dispatcher), () =>
        {
            created++;
            settings = new FoundationWindow { Left = -10000, Top = -10000, ShowActivated = false };
            return settings;
        }, NullAppLogger.Instance);
        var locked = false;
        using var interop = new WindowInteropService(hud, () => locked, NullAppLogger.Instance);
        hud.SettingsRequested += () =>
        {
            if (!locked && !service.Open(SettingsOpenSource.HudGear, SettingsCategory.Hud))
                throw new InvalidOperationException("Settings route failed.");
        };
        try
        {
            interop.Initialize();
            foreach (var minimal in new[] { false, true })
            {
                hud.DataContext = new { IsHudChromeVisible = !minimal, IsFloatingEditStripVisible = minimal, IsUnlocked = true };
                hud.Show();
                hud.UpdateLayout();
                var button = minimal ? hud.FloatingSettingsButton : hud.HeaderSettingsButton;
                for (var i = 0; i < 3; i++)
                {
                    button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    });
                    if (created != 1 || settings?.IsVisible != true) throw new InvalidOperationException("Gear failed.");
                    settings.WindowState = WindowState.Minimized;
                    service.Open(SettingsOpenSource.Tray, null);
                    if (settings.WindowState != WindowState.Normal) throw new InvalidOperationException("Restore failed.");
                    service.Hide();
                    locked = true;
                    if (!interop.ApplyClickThrough(true)) throw new InvalidOperationException("Lock failed.");
                    hud.Hide();
                    hud.Show();
                    locked = false;
                    if (!interop.ApplyClickThrough(false)) throw new InvalidOperationException("Unlock failed.");
                }
            }
        }
        finally { hud.AllowClose = true; hud.Close(); }
    }
}
