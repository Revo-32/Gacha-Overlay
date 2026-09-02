using System.Globalization;

namespace LSOverlay.Backend.Configuration;

internal sealed record BackendDeploymentOptions(
    bool IsRailway,
    Uri ListenUri,
    string DataDirectory,
    string? VolumeMountPath)
{
    public static BackendDeploymentOptions Resolve(Func<string, string?> environment)
    {
        string? Read(string key)
        {
            try { return environment(key)?.Trim(); }
            catch (KeyNotFoundException) { return null; }
        }

        var mount = Read("RAILWAY_VOLUME_MOUNT_PATH");
        var railway = new[] { "RAILWAY_PROJECT_ID", "RAILWAY_ENVIRONMENT_ID", "RAILWAY_SERVICE_ID" }
            .Any(key => !string.IsNullOrWhiteSpace(Read(key))) || !string.IsNullOrWhiteSpace(mount);
        var explicitData = Read("LSO_BACKEND_DATA_DIR");
        var legacyData = Read(BackendEnvironmentVariables.StateDirectory);
        var data = First(explicitData, legacyData, mount) ?? Path.Combine(AppContext.BaseDirectory, "state");
        if (railway)
        {
            if (string.IsNullOrWhiteSpace(mount) || !Path.IsPathFullyQualified(mount) ||
                !Path.IsPathFullyQualified(data))
            {
                throw new BackendDeploymentException("Railway requires an attached persistent Volume and an absolute data directory.");
            }

            mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount));
            data = Path.GetFullPath(data);
            if (mount == Path.GetPathRoot(mount) || !IsWithin(mount, data))
            {
                throw new BackendDeploymentException("Railway data directory must stay inside its attached Volume.");
            }
        }

        var explicitListen = Read(BackendEnvironmentVariables.ListenUrl);
        var aspnetUrls = Read("ASPNETCORE_URLS");
        Uri listen;
        if (railway)
        {
            if (!int.TryParse(Read("PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65535)
            {
                throw new BackendDeploymentException("Railway PORT must be an integer between 1 and 65535.");
            }

            if (!string.IsNullOrWhiteSpace(explicitListen))
            {
                throw new BackendDeploymentException("Remove LSO_LISTEN_URL on Railway; PORT is the production listener authority.");
            }

            // Exec-form containers do not perform shell expansion. Accept the user's
            // Railway guide template as well as its already-expanded form explicitly.
            var expanded = aspnetUrls?
                .Replace("${{PORT}}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("${PORT}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(expanded) &&
                expanded.TrimEnd('/') != $"http://+:{port}" &&
                expanded.TrimEnd('/') != $"http://*:{port}" &&
                expanded.TrimEnd('/') != $"http://0.0.0.0:{port}")
            {
                throw new BackendDeploymentException("Railway ASPNETCORE_URLS must be one internal HTTP wildcard listener using PORT.");
            }

            listen = new Uri($"http://0.0.0.0:{port}");
        }
        else
        {
            var url = First(explicitListen, aspnetUrls) ?? "http://127.0.0.1:5188";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                !LSOverlay.Protocol.TransportEndpointSecurity.IsAllowed(parsed) ||
                parsed.Scheme is not ("http" or "https"))
            {
                throw new BackendDeploymentException("Local Backend listener must use HTTPS or loopback HTTP.");
            }

            listen = parsed;
        }

        return new BackendDeploymentOptions(railway, listen, Path.GetFullPath(data), mount);
    }

    internal static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

// Messages are fixed, value-free configuration guidance; safe to show at startup.
internal sealed class BackendDeploymentException(string message) : ArgumentException(message);
