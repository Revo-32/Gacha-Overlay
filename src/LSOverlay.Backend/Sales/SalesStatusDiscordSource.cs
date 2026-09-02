using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using LSOverlay.Backend.Chat;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Sales;

internal enum SalesStatusDiscordResult
{
    Success,
    AccessDenied,
    NotFound,
    RateLimited,
    Unavailable,
}

internal sealed record SalesStatusMessageSnapshot(
    ulong MessageId,
    ulong AuthorId,
    SalesCompletionObservation Observation,
    object NativeHandle);

internal sealed record SalesStatusMessageResult(
    SalesStatusDiscordResult Status,
    SalesStatusMessageSnapshot? Message);

internal interface ISalesStatusDiscordSource
{
    Task<SalesStatusMessageResult> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken);

    Task<SalesStatusDiscordResult> AddOwnReactionAsync(
        SalesStatusMessageSnapshot message,
        SalesStatus status,
        CancellationToken cancellationToken);

    Task<SalesStatusDiscordResult> RemoveOwnReactionAsync(
        SalesStatusMessageSnapshot message,
        SalesStatus status,
        CancellationToken cancellationToken);
}

internal sealed class DiscordNetSalesStatusSource : ISalesStatusDiscordSource
{
    private readonly DiscordSocketClient _client;
    private readonly IChatDiscordSource _chat;

    public DiscordNetSalesStatusSource(
        DiscordSocketClient client,
        IChatDiscordSource chat)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    public async Task<SalesStatusMessageResult> GetMessageAsync(
        ulong channelId,
        ulong messageId,
        CancellationToken cancellationToken)
    {
        var result = await _chat.GetMessageAsync(channelId, messageId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            ChatSourceStatus.Available when result.Message is not null =>
                new SalesStatusMessageResult(
                    SalesStatusDiscordResult.Success,
                    new SalesStatusMessageSnapshot(
                        result.Message.Id,
                        result.Message.Author.Id,
                        RemoteSalesService.CreateObservation(
                            result.Message,
                            SalesEvidenceCoverage.Complete),
                        result.Message)),
            ChatSourceStatus.NotFound => new SalesStatusMessageResult(
                SalesStatusDiscordResult.NotFound,
                null),
            _ => new SalesStatusMessageResult(
                SalesStatusDiscordResult.Unavailable,
                null),
        };
    }

    public Task<SalesStatusDiscordResult> AddOwnReactionAsync(
        SalesStatusMessageSnapshot message,
        SalesStatus status,
        CancellationToken cancellationToken) =>
        MutateAsync(message, status, remove: false, cancellationToken);

    public Task<SalesStatusDiscordResult> RemoveOwnReactionAsync(
        SalesStatusMessageSnapshot message,
        SalesStatus status,
        CancellationToken cancellationToken) =>
        MutateAsync(message, status, remove: true, cancellationToken);

    private async Task<SalesStatusDiscordResult> MutateAsync(
        SalesStatusMessageSnapshot snapshot,
        SalesStatus status,
        bool remove,
        CancellationToken cancellationToken)
    {
        if (snapshot.NativeHandle is not IMessage message)
        {
            return SalesStatusDiscordResult.Unavailable;
        }

        try
        {
            var options = new RequestOptions { CancelToken = cancellationToken };
            var emote = CreateEmote(status);
            if (remove)
            {
                // Passing the authenticated Bot user is translated by Discord.Net to
                // Discord's DELETE .../reactions/{emoji}/@me route. It can never
                // remove a human user's reaction.
                await message.RemoveReactionAsync(
                        emote,
                        _client.Rest.CurrentUser.Id,
                        options)
                    .ConfigureAwait(false);
            }
            else
            {
                await message.AddReactionAsync(emote, options).ConfigureAwait(false);
            }

            return SalesStatusDiscordResult.Success;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.NotFound)
        {
            return SalesStatusDiscordResult.NotFound;
        }
        catch (HttpException exception) when (exception.HttpCode == HttpStatusCode.Forbidden)
        {
            return SalesStatusDiscordResult.AccessDenied;
        }
        catch (HttpException exception) when (
            exception.HttpCode == HttpStatusCode.TooManyRequests)
        {
            return SalesStatusDiscordResult.RateLimited;
        }
        catch (Exception exception) when (
            exception is HttpException or TimeoutException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SalesStatusDiscordResult.Unavailable;
        }
    }

    private static Emote CreateEmote(SalesStatus status) => status switch
    {
        SalesStatus.Selling => new Emote(
            RemoteSalesPolicy.SellingEmojiId,
            RemoteSalesPolicy.SellingEmojiName,
            animated: false),
        SalesStatus.Negotiating => new Emote(
            RemoteSalesPolicy.NegotiatingEmojiId,
            RemoteSalesPolicy.NegotiatingEmojiName,
            animated: false),
        SalesStatus.Completed => new Emote(
            RemoteSalesPolicy.SoldEmojiId,
            RemoteSalesPolicy.SoldEmojiName,
            animated: false),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
