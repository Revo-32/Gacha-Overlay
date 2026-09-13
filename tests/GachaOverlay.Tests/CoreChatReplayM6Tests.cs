using System.Reflection;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests;

public sealed class CoreChatReplayM6Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ReadyPreservesSnapshotCursorThenDeliversEveryStagedAndLiveMutation(int count)
    {
        await using var client = new LSOverlay.RemoteClient.LSOverlayRemoteClient(new Uri("http://127.0.0.1:1"));
        var descriptor = new ChatChannelDescriptor(1, 10, "synthetic", 0, false);
        var bootstrap = new ChatBootstrapResponse(1, descriptor, "fixture", 3, []);
        var type = typeof(LSOverlay.RemoteClient.LSOverlayRemoteClient).GetNestedType("TransactionalChatSwitchState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(type, client)!;
        object Field(string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        type.GetField("_latestRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, 0L);
        ((Dictionary<long, ChatBootstrapResponse>)Field("_requested"))[0] = bootstrap;
        ((Dictionary<long, List<ChatMutationEnvelope>>)Field("_staged"))[0] = [];
        long cursor = -1;
        var delivered = 0;
        client.ChatChannelReady += value => { Assert.Same(bootstrap.RecentMessages, value.RecentMessages); cursor = value.LatestSequence; };
        client.ChatMutationReceived += value => { Assert.Equal(cursor + 1, value.Sequence); cursor = value.Sequence; delivered++; };
        void Accept(int offset) => type.GetMethod("Accept")!.Invoke(state, [new StreamServerMessage(1,
            OverlayTransportProtocol.ChatMessageDelete, SwitchGeneration: 0,
            ChatEvent: new ChatMutationEnvelope(1, "fixture", 3 + offset, OverlayTransportProtocol.ChatMessageDelete, 10, (ulong)offset, null))]);
        for (var i = 1; i <= count; i++) Accept(i);
        type.GetMethod("Commit")!.Invoke(state, [new StreamServerMessage(1, OverlayTransportProtocol.ChatReady,
            ChannelId: 10, ChatGeneration: "fixture", ChatLatestSequence: 3 + count, SwitchGeneration: 0)]);
        Assert.Equal(3 + count, cursor);
        Assert.Equal(count, delivered);
        Accept(count + 1);
        Assert.Equal(4 + count, cursor);
        Assert.Equal(count + 1, delivered);
    }
}
