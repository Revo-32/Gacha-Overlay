using System.IO;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class LegacyCredentialRetirementService
{
    private readonly string[] _exactLegacyPaths;
    private readonly IAppLogger _logger;

    public LegacyCredentialRetirementService(
        string legacyClientSecretPath,
        string legacyOAuthTokenPath,
        IAppLogger logger)
    {
        _exactLegacyPaths =
        [
            Path.GetFullPath(legacyClientSecretPath),
            Path.GetFullPath(legacyOAuthTokenPath),
        ];
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool Retire()
    {
        var succeeded = true;
        foreach (var path in _exactLegacyPaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                File.Delete(path);
                _logger.Information(
                    "MIGRATION",
                    $"Retired obsolete protected credential file kind={Path.GetFileName(path)}.");
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                succeeded = false;
                _logger.Warning(
                    "MIGRATION",
                    $"Obsolete credential retirement failed kind={Path.GetFileName(path)} " +
                    $"error={exception.GetType().Name}.");
            }
        }

        return succeeded;
    }
}
