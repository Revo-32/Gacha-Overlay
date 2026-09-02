using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.WebAuth;

internal static class WebAuthLogPolicy
{
    public static void Apply(IServiceCollection services) => services.PostConfigure<LoggerFilterOptions>(options =>
    {
        // Request-start logging precedes middleware and includes the raw query.
        // Deployment logging overrides must not re-enable that channel (nor body/header logging).
        for (var i = options.Rules.Count - 1; i >= 0; i--)
            if (options.Rules[i].CategoryName?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true)
                options.Rules.RemoveAt(i);
        options.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore", LogLevel.Warning, null));
        options.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None, null));
        options.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.HttpLogging", LogLevel.None, null));
    });
}
