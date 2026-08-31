using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GachaOverlay.App.Services.Sales;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;

namespace GachaOverlay.Tests.Sales.Uia;

public sealed class DiscordUiaObservationSourceTests
{
    [Fact]
    public void Source_StartsDisabled()
    {
        using var adapter = new ScriptedAccessibilityAdapter();
        using var source = CreateSource(adapter);
        Assert.False(source.IsRunning);
        Assert.Equal(SalesObservationStatus.Disabled, source.Status);
    }

    [Fact]
    public void Start_WithReadyTargets_PerformsInitialResync()
    {
        using var adapter = CompleteAdapter("1");
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Live));
        Assert.True(adapter.ScanCount >= 1);
        Assert.True(adapter.Requests[0].FullResyncRequested);
    }

    [Fact]
    public void SourceNotReady_DoesNotCallUiaAdapter()
    {
        using var adapter = CompleteAdapter();
        using var source = CreateSource(adapter);
        source.UpdateTargets(SalesObservationTargetSet.Empty);
        source.Start();
        Thread.Sleep(80);
        Assert.Equal(0, adapter.ScanCount);
        Assert.Equal(SalesObservationReason.SourceNotReady, source.Health.Reason);
    }

    [Theory]
    [InlineData(SalesObservationReason.DiscordNotRunning)]
    [InlineData(SalesObservationReason.DiscordWindowNotFound)]
    public void DiscordUnavailable_ReportsUnavailable(SalesObservationReason reason)
    {
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, _) => UiaSalesTestFactory.Unavailable(reason),
        };
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Unavailable));
        Assert.Equal(reason, source.Health.Reason);
        Assert.False(source.Health.DiscordWindowAvailable);
    }

    [Fact]
    public void AccessibilityMissing_ReportsAccessibilityUnavailable()
    {
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, _) => UiaSalesTestFactory.Selected() with
            {
                AccessibilityReady = false,
                FailureReason = SalesObservationReason.AccessibilityTreeUnavailable,
            },
        };
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(WaitUntil(() =>
            source.Status == SalesObservationStatus.AccessibilityUnavailable));
        Assert.Equal(
            SalesObservationReason.AccessibilityTreeUnavailable,
            source.Health.Reason);
    }

    [Fact]
    public void OtherChannel_ReportsPausedAndPublishesNoTrustedObservations()
    {
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, _) => UiaSalesTestFactory.Selected() with
            {
                TargetChannelStatus = SalesTargetChannelStatus.NotSelected,
                FailureReason = SalesObservationReason.TargetChannelNotSelected,
            },
        };
        using var source = CreateSource(adapter);
        var batches = new ConcurrentQueue<SalesObservationBatch>();
        source.BatchAvailable += batches.Enqueue;
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Paused));
        Assert.DoesNotContain(batches, batch => batch.IsTrusted);
    }

    [Fact]
    public void PausedToTargetChannel_TransitionsThroughResyncingToLive()
    {
        using var adapter = new ScriptedAccessibilityAdapter();
        adapter.Enqueue(UiaSalesTestFactory.Selected() with
        {
            TargetChannelStatus = SalesTargetChannelStatus.NotSelected,
            FailureReason = SalesObservationReason.TargetChannelNotSelected,
        });
        adapter.DefaultResponse = (_, _) => UiaSalesTestFactory.Selected(new[]
        {
            UiaSalesTestFactory.Context("1"),
        });
        using var source = CreateSource(adapter);
        var statuses = new ConcurrentQueue<SalesObservationStatus>();
        source.BatchAvailable += batch => statuses.Enqueue(batch.SensorStatus);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(WaitUntil(() => statuses.Contains(SalesObservationStatus.Paused)));
        source.RequestFullResync();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Live));
        var sequence = statuses.ToArray();
        var paused = Array.IndexOf(sequence, SalesObservationStatus.Paused);
        var live = Array.LastIndexOf(sequence, SalesObservationStatus.Live);
        Assert.Contains(
            SalesObservationStatus.Resyncing,
            sequence.Skip(paused + 1).Take(live - paused - 1));
    }

    [Fact]
    public void PartialCoverage_IsNeverReportedLive()
    {
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, _) => UiaSalesTestFactory.Selected(new[]
            {
                UiaSalesTestFactory.Context("1"),
            }),
        };
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1", "2"));
        source.Start();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Partial));
        Assert.Equal(SalesCoverageState.Partial, source.Health.Coverage);
        Assert.False(source.Health.IsComplete);
    }

    [Fact]
    public void PollingIsSingleFlight()
    {
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, token) =>
            {
                entered.Set();
                gate.Wait(token);
                return UiaSalesTestFactory.Selected();
            },
        };
        using var source = CreateSource(adapter, pollInterval: TimeSpan.FromMilliseconds(10));
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        for (var index = 0; index < 10; index++)
        {
            source.RequestFullResync();
        }

        Thread.Sleep(80);
        Assert.Equal(1, adapter.MaximumConcurrentScans);
        gate.Set();
        Assert.True(WaitUntil(() => adapter.ScanCount >= 2));
    }

    [Fact]
    public void MultipleRequestsDuringScan_CoalesceToOnePendingScan()
    {
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var adapter = new ScriptedAccessibilityAdapter();
        adapter.Enqueue((_, token) =>
        {
            entered.Set();
            gate.Wait(token);
            return UiaSalesTestFactory.Selected();
        });
        adapter.DefaultResponse = (_, _) => UiaSalesTestFactory.Selected();
        using var source = CreateSource(adapter, pollInterval: TimeSpan.FromSeconds(5));
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        for (var index = 0; index < 20; index++)
        {
            source.RequestFullResync();
        }

        gate.Set();
        Assert.True(WaitUntil(() => adapter.ScanCount == 2));
        Thread.Sleep(100);
        Assert.Equal(2, adapter.ScanCount);
        Assert.True(source.Health.CoalescedRequestCount > 0);
    }

    [Fact]
    public void TargetChangeDuringScan_MarksResultPartialAndSchedulesRescan()
    {
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var adapter = new ScriptedAccessibilityAdapter();
        adapter.Enqueue((_, token) =>
        {
            entered.Set();
            gate.Wait(token);
            return UiaSalesTestFactory.Selected(new[]
            {
                UiaSalesTestFactory.Context("1"),
            });
        });
        adapter.DefaultResponse = (request, _) => UiaSalesTestFactory.Selected(
            request.TargetSet.Targets.Select(target =>
                UiaSalesTestFactory.Context(target.MessageId)).ToArray());
        using var source = CreateSource(adapter, pollInterval: TimeSpan.FromSeconds(5));
        var batches = new ConcurrentQueue<SalesObservationBatch>();
        source.BatchAvailable += batches.Enqueue;
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        source.UpdateTargets(UiaSalesTestFactory.Targets(2, "1", "2"));
        gate.Set();
        Assert.True(WaitUntil(() => batches.Any(batch =>
            batch.StatusReason == SalesObservationReason.SourceChangedDuringScan)));
        Assert.True(WaitUntil(() =>
            source.Status == SalesObservationStatus.Live &&
            source.Health.TargetMessageCount == 2));
    }

    [Fact]
    public void TargetChangeAfterLive_ImmediatelyLeavesLiveUntilFreshCoverage()
    {
        using var gate = new ManualResetEventSlim(initialState: true);
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (request, token) =>
            {
                gate.Wait(token);
                return UiaSalesTestFactory.Selected(
                    request.TargetSet.Targets.Select(target =>
                        UiaSalesTestFactory.Context(target.MessageId)).ToArray());
            },
        };
        using var source = CreateSource(adapter, pollInterval: TimeSpan.FromSeconds(5));
        source.UpdateTargets(UiaSalesTestFactory.Targets(1, "1"));
        source.Start();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Live));
        gate.Reset();
        source.UpdateTargets(UiaSalesTestFactory.Targets(2, "1", "2"));
        Assert.Equal(SalesObservationStatus.Resyncing, source.Status);
        gate.Set();
        Assert.True(WaitUntil(() =>
            source.Status == SalesObservationStatus.Live &&
            source.Health.TargetMessageCount == 2));
    }

    [Fact]
    public void Stop_CancelsPendingScanAndReleasesSession()
    {
        using var entered = new ManualResetEventSlim();
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, token) =>
            {
                entered.Set();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return UiaSalesTestFactory.Selected();
            },
        };
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        source.Stop();
        Assert.False(source.IsRunning);
        Assert.Equal(SalesObservationStatus.Disabled, source.Status);
        Assert.True(adapter.ResetCount >= 2);
    }

    [Fact]
    public void StopThenStart_UsesFreshSessionGeneration()
    {
        using var adapter = CompleteAdapter();
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Live));
        var first = source.Health.SessionGeneration;
        source.Stop();
        source.Start();
        Assert.True(WaitUntil(() =>
            source.Status == SalesObservationStatus.Live &&
            source.Health.SessionGeneration > first));
    }

    [Fact]
    public void Dispose_StopsWorkerAndDisposesAdapter()
    {
        var adapter = CompleteAdapter();
        var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(WaitUntil(() => adapter.ScanCount > 0));
        source.Dispose();
        Assert.False(source.IsRunning);
        Assert.Equal(1, adapter.DisposeCount);
    }

    [Fact]
    public void WorkerThread_IsBackgroundStaAndDoesNotUseWpfDispatcher()
    {
        ApartmentState? apartment = null;
        bool? background = null;
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, _) =>
            {
                apartment = Thread.CurrentThread.GetApartmentState();
                background = Thread.CurrentThread.IsBackground;
                return UiaSalesTestFactory.Selected();
            },
        };
        using var source = CreateSource(adapter);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(WaitUntil(() => apartment.HasValue));
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.True(background);
    }

    [Theory]
    [InlineData(typeof(COMException))]
    [InlineData(typeof(InvalidOperationException))]
    public void AdapterFailure_IsContainedAndNextPollRecovers(Type exceptionType)
    {
        using var adapter = new ScriptedAccessibilityAdapter();
        adapter.Enqueue((_, _) => throw (Exception)Activator.CreateInstance(exceptionType)!);
        adapter.DefaultResponse = (_, _) => UiaSalesTestFactory.Selected();
        using var source = CreateSource(adapter, pollInterval: TimeSpan.FromMilliseconds(20));
        var statuses = new ConcurrentQueue<SalesObservationStatus>();
        source.BatchAvailable += batch => statuses.Enqueue(batch.SensorStatus);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Assert.True(WaitUntil(() => statuses.Contains(SalesObservationStatus.Error)));
        Assert.True(WaitUntil(() => source.Status == SalesObservationStatus.Live));
        Assert.True(adapter.ResetCount >= 2);
    }

    [Fact]
    public void RepeatedUnavailableState_UsesBoundedRetry()
    {
        using var adapter = new ScriptedAccessibilityAdapter
        {
            DefaultResponse = (_, _) => UiaSalesTestFactory.Unavailable(),
        };
        var options = new DiscordUiaSensorOptions(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromSeconds(1));
        using var source = new DiscordUiaSalesReactionObservationSource(
            adapter,
            NullAppLogger.Instance,
            options: options);
        source.UpdateTargets(UiaSalesTestFactory.Targets(1));
        source.Start();
        Thread.Sleep(210);
        Assert.InRange(adapter.ScanCount, 2, 5);
    }

    private static ScriptedAccessibilityAdapter CompleteAdapter(params string[] messageIds)
    {
        var adapter = new ScriptedAccessibilityAdapter();
        adapter.DefaultResponse = (request, _) => UiaSalesTestFactory.Selected(
            request.TargetSet.Targets.Select(target =>
                UiaSalesTestFactory.Context(target.MessageId)).ToArray());
        return adapter;
    }

    private static DiscordUiaSalesReactionObservationSource CreateSource(
        ScriptedAccessibilityAdapter adapter,
        TimeSpan? pollInterval = null)
    {
        var poll = pollInterval ?? TimeSpan.FromMilliseconds(25);
        return new DiscordUiaSalesReactionObservationSource(
            adapter,
            NullAppLogger.Instance,
            options: new DiscordUiaSensorOptions(
                poll,
                poll,
                poll,
                TimeSpan.FromMilliseconds(Math.Max(100, poll.TotalMilliseconds)),
                TimeSpan.FromSeconds(2)));
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}
