using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Caching;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Localization;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Security;
using LSOverlay.Protocol;

namespace GachaOverlay.Tests.Presentation;

public sealed class M912ResourceLifetimeTests
{
    [Fact]
    public async Task Cache_BoundsOutstandingLoadsAsWellAsCompletedValues()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new BoundedAsyncCache<string>(2, _ => completion.Task);
        var requests = Enumerable.Range(0, 20).Select(i => cache.GetAsync(i.ToString())).ToArray();
        try
        {
            Assert.InRange(cache.InFlightCount, 0, 2);
        }
        finally
        {
            completion.TrySetResult("value");
            await Task.WhenAll(requests);
        }
    }

    [Fact]
    public async Task Cache_EvictionHasNoCompletedTaskSideOwner()
    {
        using var cache = new BoundedAsyncCache<string>(2, key => Task.FromResult<string?>(key));
        for (var i = 0; i < 40; i++)
        {
            await cache.GetAsync(i.ToString());
        }
        Assert.Equal(2, cache.Count);
        Assert.Equal(0, CollectionCount(cache, "_inFlight"));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.FailureCooldownCount);
    }

    [Fact]
    public async Task Cache_ClearCannotBypassOutstandingLoadBound()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new BoundedAsyncCache<string>(1, _ => completion.Task);
        var original = cache.GetAsync("old");
        for (var i = 0; i < 10; i++)
        {
            cache.Clear();
            Assert.Null(await cache.GetAsync($"new-{i}"));
            Assert.Equal(1, cache.InFlightCount);
        }
        completion.SetResult("old");
        Assert.Equal("old", await original);
        Assert.Equal(0, cache.InFlightCount);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Bitmap_OnLoadReleasesEncodedBufferAndPreservesPixels() => RunSta(() =>
    {
        var pixels = new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 };
        var original = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(original));
        var encoded = new MemoryStream();
        encoder.Save(encoded);
        encoded.Position = 0;
        var image = Assert.IsType<BitmapImage>(DiscordMediaAssetService.DecodeImage(encoded, 2));
        Assert.True(image.IsFrozen);
        var stream = Assert.IsType<BitmapDecodeStream>(image.StreamSource);
        Assert.False(stream.CanRead);
        Assert.Null(typeof(BitmapDecodeStream).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream));
        var decoded = new byte[8];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(decoded, 8, 0);
        Assert.Equal(pixels, decoded);
    });

    [Fact]
    public void ChatView_UnloadDetachesAndAbortsPendingScroll() => RunSta(() =>
    {
        var viewModel = new ChatViewModel();
        var view = new ChatView { DataContext = viewModel };
        for (var i = 0; i < 5; i++)
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, HandlerCount(viewModel, "ScrollToLatestRequested"));
            viewModel.RequestScrollToLatest();
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, HandlerCount(viewModel, "ScrollToLatestRequested"));
            Assert.Equal(0, HandlerCount(viewModel, "MentionPulseRequested"));
        }
    });

    [Fact]
    public void SalesView_UnloadDetachesAndReloadDoesNotDuplicateHandlers() => RunSta(() =>
    {
        var viewModel = new SalesQueueViewModel(new ResourceLocalizationService(SupportedLocales.English));
        var view = new SalesQueueView { DataContext = viewModel };
        for (var i = 0; i < 5; i++)
        {
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, HandlerCount(viewModel, "AnimationRequested"));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, HandlerCount(viewModel, "AnimationRequested"));
        }
    });

    [Fact]
    public async Task SalesFailureState_EvictionRemovesOutOfWindowOwnership()
    {
        var viewModel = new SalesQueueViewModel(new ResourceLocalizationService(SupportedLocales.English));
        viewModel.ConfigureStatusAction((_, _, _) => Task.FromResult<SalesStatusActionResponse?>(null));
        await viewModel.ExecuteStatusActionAsync("42", SalesStatus.Clear);
        Assert.Equal(1, CollectionCount(viewModel, "_failedStatusActions"));
        viewModel.ApplyRemoteStatusContext(new Dictionary<string, SalesCompletionObservation>(), EffectiveSalesSource.RemotePrimary);
        Assert.Equal(0, CollectionCount(viewModel, "_failedStatusActions"));
        Assert.Equal(0, CollectionCount(viewModel, "_pendingStatusActions"));
    }

    [Fact]
    public async Task Authorization_BoundsCoalescedRefreshesIndependentlyOfLeases()
    {
        var source = new BlockedGuildSource();
        var authorization = new ChatAuthorizationService(source);
        var pending = Enumerable.Range(1, ChatAuthorizationService.MaximumLeases)
            .Select(i => authorization.GetCatalogAsync(new AuthenticatedClientIdentity(Guid.NewGuid(), (ulong)i, 1), CancellationToken.None)).ToArray();
        try
        {
            var overflow = authorization.GetCatalogAsync(new AuthenticatedClientIdentity(Guid.NewGuid(), 9000, 1), CancellationToken.None);
            Assert.True(overflow.IsCompleted);
            Assert.Equal(ChatAuthorizationStatus.AuthorizationUnavailable, (await overflow).Status);
        }
        finally
        {
            source.Gate.TrySetResult(new ChatGuildSourceResult(ChatSourceStatus.Unavailable, null));
            await Task.WhenAll(pending);
        }
        Assert.Equal(0, CollectionCount(authorization, "_refreshes"));
    }

    private sealed class BlockedGuildSource : IChatDiscordSource
    {
        public TaskCompletionSource<ChatGuildSourceResult> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatGuildSourceResult> GetGuildAsync(AuthenticatedClientIdentity identity, CancellationToken cancellationToken) => Gate.Task;
        public Task<ChatMessagesSourceResult> GetRecentMessagesAsync(ulong channelId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ChatMessageSourceResult> GetMessageAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void TextRenderer_UnloadDetachesTokensAndReloadSubscribesExactlyOnce() => RunSta(() =>
    {
        var token = new ChatTokenViewModel(new ChatToken(ChatTokenKind.Text, "text"));
        var tokens = new ObservableCollection<ChatTokenViewModel> { token };
        var control = new CrispOutlinedText { Tokens = tokens };
        for (var i = 0; i < 5; i++)
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, HandlerCount(token, "PropertyChanged"));
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, HandlerCount(token, "PropertyChanged"));
            Assert.Equal(0, CollectionCount(control, "_subscribedTokens"));
        }
    });

    [Fact]
    public async Task RemoteRequestScope_RetirementCancelsAndJoinsEveryRequest()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new RemoteRequestScope(CancellationToken.None);
        var request = scope.TryRun(RemoteRequestKind.ChannelSwitch, async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.TrySetResult(); }
            return true;
        });

        Assert.NotNull(request);
        await scope.DisposeAsync();
        await stopped.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request!);
        Assert.Null(scope.TryRun(RemoteRequestKind.ChannelSwitch, _ => Task.FromResult(true)));
    }

    [Fact]
    public async Task RemoteRequestScope_BoundsOneOperationPerKind()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = new RemoteRequestScope(CancellationToken.None);
        var first = scope.TryRun(RemoteRequestKind.SalesResync, _ => gate.Task);
        var duplicate = scope.TryRun(RemoteRequestKind.SalesResync, _ => Task.FromResult(true));
        Assert.NotNull(first);
        Assert.Null(duplicate);
        gate.SetResult(true);
        Assert.True(await first!);
    }

    [Fact]
    public async Task RemoteRequestScope_RejectsLateResultFromNonCooperativeRequest()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new RemoteRequestScope(CancellationToken.None);
        var request = scope.TryRun(RemoteRequestKind.ChannelSwitch, _ => gate.Task)!;
        var retirement = scope.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        gate.SetResult(true);
        await retirement;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await scope.DisposeAsync();
    }

    private static int CollectionCount(object owner, string field)
    {
        var collection = owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;
        return (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
    }

    private static int HandlerCount(object owner, string field) =>
        ((Delegate?)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner))?.GetInvocationList().Length ?? 0;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
