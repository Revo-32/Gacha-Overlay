using System.Reflection;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests;

public sealed class SalesReplay211Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void BootstrapCursorMustNotJumpOverStagedReplay(int count)
    {
        using var http = new HttpClient();
        var client = new LSOverlay.RemoteClient.LSOverlayRemoteClient(new Uri("http://127.0.0.1:5188"), http);
        var bootstrap = new SalesBootstrapResponse(OverlayTransportProtocol.Version, null!, "fixture", 3,
            [], [], SalesBootstrapCoverage.Complete);
        var type = typeof(LSOverlay.RemoteClient.LSOverlayRemoteClient).GetNestedType("TransactionalSalesState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(type, client, bootstrap)!;
        long cursor = -1;
        client.SalesReady += value => cursor = value.LatestSequence;
        client.SalesMutationReceived += value =>
        {
            Assert.Equal(cursor + 1, value.Sequence);
            cursor = value.Sequence;
        };
        for (var i = 1; i <= count; i++)
        {
            var envelope = new SalesMutationEnvelope(OverlayTransportProtocol.Version, "fixture", 3 + i,
                OverlayTransportProtocol.SalesMessageDelete, 1, (ulong)i, null, null);
            type.GetMethod("Accept")!.Invoke(state, [new StreamServerMessage(OverlayTransportProtocol.Version,
                envelope.EventType, SalesEvent: envelope)]);
        }
        type.GetMethod("Commit")!.Invoke(state, [new StreamServerMessage(OverlayTransportProtocol.Version,
            OverlayTransportProtocol.SalesReady, SalesGeneration: "fixture", SalesLatestSequence: 3 + count)]);
        Assert.Equal(3 + count, cursor);
    }
}
