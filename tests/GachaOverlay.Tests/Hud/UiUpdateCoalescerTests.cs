using GachaOverlay.Core.Hud.Presentation;

namespace GachaOverlay.Tests.Hud;

public sealed class UiUpdateCoalescerTests
{
    [Fact]
    public void MultipleRequestsInOneCycle_AreExecutedOnce()
    {
        var scheduler = new ManualScheduler();
        var executions = new List<int>();
        using var coalescer = new UiUpdateCoalescer(scheduler, executions.Add);

        coalescer.Request();
        coalescer.Request();
        coalescer.Request();

        Assert.Single(scheduler.Pending);
        scheduler.RunNext();
        Assert.Equal(new[] { 3 }, executions);
    }

    [Fact]
    public void RequestAfterExecution_SchedulesAnotherCycle()
    {
        var scheduler = new ManualScheduler();
        var executions = 0;
        using var coalescer = new UiUpdateCoalescer(scheduler, _ => executions++);

        coalescer.Request();
        scheduler.RunNext();
        coalescer.Request();
        scheduler.RunNext();

        Assert.Equal(2, executions);
    }

    [Fact]
    public void Dispose_PreventsPendingCallbackFromMutatingPresentation()
    {
        var scheduler = new ManualScheduler();
        var executions = 0;
        var coalescer = new UiUpdateCoalescer(scheduler, _ => executions++);
        coalescer.Request();

        coalescer.Dispose();
        scheduler.RunNext();

        Assert.Equal(0, executions);
        Assert.False(coalescer.Request());
    }

    [Fact]
    public void CallbackException_DoesNotBreakFutureRequests()
    {
        var scheduler = new ManualScheduler();
        var attempts = 0;
        var errors = 0;
        using var coalescer = new UiUpdateCoalescer(
            scheduler,
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("expected");
                }
            },
            _ => errors++);

        coalescer.Request();
        scheduler.RunNext();
        coalescer.Request();
        scheduler.RunNext();

        Assert.Equal(2, attempts);
        Assert.Equal(1, errors);
    }

    private sealed class ManualScheduler : IUiCallbackScheduler
    {
        public Queue<Action> Pending { get; } = new();

        public void Schedule(Action callback) => Pending.Enqueue(callback);

        public void RunNext() => Pending.Dequeue()();
    }
}
