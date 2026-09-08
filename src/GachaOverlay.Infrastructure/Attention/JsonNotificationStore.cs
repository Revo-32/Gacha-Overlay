using System.Text.Json;
using GachaOverlay.Core.Attention;

namespace GachaOverlay.Infrastructure.Attention;

public sealed class JsonNotificationStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    public AttentionHistoryState? Load()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 128 * 1024) return null;
            var state = JsonSerializer.Deserialize<AttentionHistoryState>(stream);
            return state is { Version: 1, Items.Count: <= NotificationHistory.Capacity } ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public bool Save(AttentionHistoryState state)
    {
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
