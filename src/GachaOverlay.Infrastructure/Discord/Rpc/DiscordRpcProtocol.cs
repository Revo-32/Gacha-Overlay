using System.Text.Json;

namespace GachaOverlay.Infrastructure.Discord.Rpc;

public static class DiscordRpcProtocol
{
    public static void EnsureSuccess(JsonElement payload)
    {
        if (!payload.TryGetProperty("evt", out var eventElement) ||
            !string.Equals(eventElement.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var code = "unknown";
        var message = "Unknown RPC error";
        if (payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("code", out var codeElement))
            {
                code = codeElement.ToString();
            }

            if (data.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString() ?? message;
            }
        }

        throw new DiscordRpcException(code, message);
    }
}
