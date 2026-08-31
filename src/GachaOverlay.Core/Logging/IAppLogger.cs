namespace GachaOverlay.Core.Logging;

public interface IAppLogger
{
    void Information(string category, string message);

    void Warning(string category, string message);

    void Error(string category, string message, Exception? exception = null);
}
