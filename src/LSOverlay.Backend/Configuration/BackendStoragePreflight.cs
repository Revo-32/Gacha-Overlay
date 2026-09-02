namespace LSOverlay.Backend.Configuration;

internal static class BackendStoragePreflight
{
    public static void Validate(BackendConfiguration configuration, Func<string, bool>? mountedVolume = null)
    {
        var root = configuration.StateDirectory;
        if (configuration.Deployment?.IsRailway == true)
        {
            var mount = configuration.Deployment.VolumeMountPath;
            if (mount is null || !Directory.Exists(mount) ||
                !BackendDeploymentOptions.IsWithin(mount, root) ||
                !(mountedVolume ?? IsLinuxMountPoint)(mount))
            {
                throw new IOException("Railway persistent Volume is missing or not mounted; startup refused.");
            }

            // Do not allow a subdirectory symlink to move credentials off the Volume.
            var cursor = new DirectoryInfo(root);
            while (cursor is not null)
            {
                if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("Railway persistent data directory must not traverse symbolic links.");
                }

                cursor = cursor.Parent;
            }
        }

        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".lso-storage-check-{Guid.NewGuid():N}.tmp");
        var moved = probe + ".moved";
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Move(probe, moved);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (File.Exists(moved)) File.Delete(moved);
        }
    }

    private static bool IsLinuxMountPoint(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return File.ReadLines("/proc/self/mountinfo").Any(line =>
        {
            var fields = line.Split(' ');
            if (fields.Length < 6) return false;
            var mount = fields[4].Replace("\\040", " ", StringComparison.Ordinal)
                .Replace("\\011", "\t", StringComparison.Ordinal)
                .Replace("\\012", "\n", StringComparison.Ordinal)
                .Replace("\\134", "\\", StringComparison.Ordinal);
            return string.Equals(Path.TrimEndingDirectorySeparator(mount), expected, StringComparison.Ordinal);
        });
    }
}
