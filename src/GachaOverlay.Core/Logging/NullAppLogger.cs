namespace GachaOverlay.Core.Logging;

public sealed class NullAppLogger : IAppLogger
{
    public static NullAppLogger Instance { get; } = new();

    private NullAppLogger()
    {
    }

    public void Information(string category, string message)
    {
    }

    public void Warning(string category, string message)
    {
    }

    public void Error(string category, string message, Exception? exception = null)
    {
    }
}
