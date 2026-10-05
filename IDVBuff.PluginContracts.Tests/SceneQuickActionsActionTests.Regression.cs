using System.Collections.Concurrent;
using System.Reflection;
using IDVBuff.PluginContracts;
using IDVBuff.Plugins.SceneQuickActions;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed partial class SceneQuickActionsActionTests
{
    [Fact]
    public async Task StopDuringQuickReplaceRecaptureRejectsThatFrameAndRestartUsesNewGeometry()
    {
        using var fixture = new Fixture();
        using var recapture = new BlockingPoint();
        using var stoppedToken = new ManualResetEventSlim();
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = PickupOptions() with { CooldownMilliseconds = 0 };
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, quick: OldHit);
        fixture.AddFrame(3, pickup: NewHit, quick: NewHit);
        fixture.AddFrame(4, quick: NewHit, bounds: new PluginClientBounds(200, 300, 200, 200));
        fixture.Grabber.OnGrab = count => { if (count == 2) recapture.Block(); };
        fixture.Input.OnClick = () => clicked.TrySetResult();
        fixture.Start(options);
        using var cancellationNotice = SessionToken(fixture).Register(stoppedToken.Set);
        Task? stop = null;
        try
        {
            Assert.True(recapture.Entered.Wait(TimeSpan.FromSeconds(5)));
            stop = Task.Run(fixture.Runner.Stop);
            Assert.True(stoppedToken.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(stop.IsCompleted);
            recapture.Release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(fixture.Input.Clicks);
            Assert.Single(fixture.Input.Bindings);

            fixture.Runner.Start();
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.Stop();
            Assert.Equal((310, 430), Assert.Single(fixture.Input.Clicks));
            Assert.Equal(2, fixture.Input.Bindings.Count);
            Assert.Equal(4, fixture.Grabber.GrabCount);
        }
        finally
        {
            recapture.Release.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task StopCancelsQueuedDropAndRestartOnlyActsOnItsFreshInvite()
    {
        using var fixture = new Fixture();
        using var wake = new BlockingPoint();
        using var stoppedToken = new ManualResetEventSlim();
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AddFrame(1, accept: OldHit);
        fixture.AddFrame(2, accept: NewHit);
        fixture.Input.OnTab = count => { if (count == 1) wake.Block(); };
        fixture.Input.OnClick = () => clicked.TrySetResult();
        fixture.Start(InviteOptions());
        using var cancellationNotice = SessionToken(fixture).Register(stoppedToken.Set);
        Task? stop = null;
        try
        {
            Assert.True(wake.Entered.Wait(TimeSpan.FromSeconds(5)));
            fixture.Hotkeys.PressBag();
            var queuedDrop = fixture.DropTask;
            Assert.False(queuedDrop.IsCompleted);
            stop = Task.Run(fixture.Runner.Stop);
            Assert.True(stoppedToken.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(stop.IsCompleted);
            wake.Release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await queuedDrop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(fixture.Input.Clicks);
            Assert.Empty(fixture.Input.DragStarts);
            Assert.Equal(["tab", "tab"], fixture.Input.Events.ToArray());

            fixture.Runner.Start();
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.Stop();
            Assert.Equal((55, 65), Assert.Single(fixture.Input.Clicks));
            Assert.Equal(2, fixture.Grabber.GrabCount);
            Assert.Empty(fixture.Input.DragStarts);
            Assert.Equal(4, fixture.Input.Events.Count(value => value == "tab"));
        }
        finally
        {
            wake.Release.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task BusyManualRequestsNeitherQueueAnotherDropNorConsumeTheNextHotbarSlot()
    {
        using var fixture = new Fixture();
        using var held = new BlockingPoint();
        fixture.Input.OnLeftDown = held.Block;
        fixture.Start(IdleOptions());
        try
        {
            fixture.Hotkeys.PressHotbar();
            var first = fixture.DropTask;
            Assert.True(held.Entered.Wait(TimeSpan.FromSeconds(5)));
            MakeHotkeyEligible(fixture);
            fixture.Hotkeys.PressBag();
            MakeHotkeyEligible(fixture);
            fixture.Hotkeys.PressHotbar();
            Assert.Same(first, fixture.DropTask);
            Assert.Equal(1, Field<int>(fixture, "_hotbarCursor"));
            Assert.Single(fixture.Input.DragStarts);
            Assert.DoesNotContain("left-up", fixture.Input.Events);
            held.Release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));

            fixture.Input.OnLeftDown = null;
            MakeHotkeyEligible(fixture);
            fixture.Hotkeys.PressHotbar();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Field<int>(fixture, "_hotbarCursor"));
            Assert.Equal([HotbarPosition(0), HotbarPosition(1)], fixture.Input.DragStarts.ToArray());
            Assert.Equal(2, fixture.Input.Events.Count(value => value == "left-up"));
            Assert.Equal(4, fixture.Input.Events.Count(value => value == "tab"));
        }
        finally { held.Release.Set(); fixture.Runner.Stop(); }
    }

    [Fact]
    public async Task CancelledPickupBeforeFirstInputRearmsButStillHonorsTheDropSilence()
    {
        using var fixture = new Fixture();
        using var wake = new BlockingPoint();
        var options = PickupOptions() with { PickupWakeMouse = true, CooldownMilliseconds = 0 };
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(3, pickup: NewHit, quick: NewHit);
        fixture.AddFrame(4, quick: NewHit);
        fixture.Input.OnTab = count => { if (count == 1) wake.Block(); };
        fixture.Start(options);
        try
        {
            Assert.True(wake.Entered.Wait(TimeSpan.FromSeconds(5)));
            fixture.Runner.SetOptions(IdleOptions()); // further automatic rounds are driven explicitly below
            fixture.Hotkeys.PressHotbar();
            wake.Release.Set();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.ProcessOnce(options, default);
            Assert.Equal(2, fixture.Grabber.GrabCount);
            Assert.Empty(fixture.Input.Bindings);
            Assert.Empty(fixture.Input.Clicks);

            // Expire only the independent 30-second pause, without waiting for
            // wall time or changing the pickup armed state under test.
            SetField(fixture, "_dropSilenceUntilTicks", 0L);
            fixture.Runner.ProcessOnce(options, default);
            Assert.Single(fixture.Input.Bindings);
            Assert.Equal((55, 65), Assert.Single(fixture.Input.Clicks));
            Assert.Equal(4, fixture.Grabber.GrabCount);
            Assert.Equal(6, fixture.Input.Events.Count(value => value == "tab"));
        }
        finally { wake.Release.Set(); fixture.Runner.Stop(); }
    }

    [Fact]
    public async Task CancelledPickupAfterFirstInputRequiresPanelDisappearanceBeforePickingAgain()
    {
        using var fixture = new Fixture();
        using var pickup = new BlockingPoint();
        var options = PickupOptions() with { CooldownMilliseconds = 0 };
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(3);
        fixture.AddFrame(4, pickup: NewHit, quick: NewHit);
        fixture.AddFrame(5, quick: NewHit);
        fixture.Input.OnBinding = _ => pickup.Block();
        fixture.Start(options);
        try
        {
            Assert.True(pickup.Entered.Wait(TimeSpan.FromSeconds(5)));
            fixture.Runner.SetOptions(IdleOptions()); // preserve armed state while removing polling races
            fixture.Hotkeys.PressHotbar();
            pickup.Release.Set();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
            SetField(fixture, "_dropSilenceUntilTicks", 0L);
            fixture.Runner.ProcessOnce(options, default);
            Assert.Single(fixture.Input.Bindings);
            Assert.Empty(fixture.Input.Clicks);
            fixture.Runner.ProcessOnce(options, default); // absent panel rearms the scene
            fixture.Input.OnBinding = null;
            fixture.Runner.ProcessOnce(options, default);
            Assert.Equal(2, fixture.Input.Bindings.Count);
            Assert.Equal((55, 65), Assert.Single(fixture.Input.Clicks));
            Assert.Equal(5, fixture.Grabber.GrabCount);
        }
        finally { pickup.Release.Set(); fixture.Runner.Stop(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupFaultIsLoggedAndCannotPoisonTheNextRestartedManualOperation(bool mouseUpFails)
    {
        using var fixture = new Fixture();
        if (mouseUpFails)
            fixture.Input.OnLeftUp = () => throw new InvalidOperationException("controlled MouseUp refusal");
        else
            fixture.Input.OnTab = count => { if (count == 2) throw new InvalidOperationException("controlled close refusal"); };
        fixture.Start(IdleOptions());
        fixture.Hotkeys.PressHotbar();
        await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Input.DragStarts);
        Assert.Equal(1, fixture.Input.Events.Count(value => value == "left-up"));
        Assert.Equal(2, fixture.Input.Events.Count(value => value == "tab"));
        Assert.Contains(fixture.Logger.Warnings, value => value.Contains(mouseUpFails ? "MouseUp refusal" : "close refusal"));

        fixture.Runner.Stop();
        fixture.Input.OnLeftUp = null;
        fixture.Input.OnTab = null;
        fixture.Runner.Start();
        fixture.Hotkeys.PressHotbar();
        await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Input.DragStarts.Count);
        Assert.Equal(2, fixture.Input.Events.Count(value => value == "left-up"));
        Assert.Equal(4, fixture.Input.Events.Count(value => value == "tab"));
    }

    private static (int X, int Y) HotbarPosition(int slot)
    {
        Assert.True(SceneQuickActionsDropPlan.TryGetSlots(1920, 1080, false, slot, out var positions));
        return ((int)Math.Round(positions[0].X * 1920), (int)Math.Round(positions[0].Y * 1080));
    }

    private static CancellationToken SessionToken(Fixture fixture) =>
        Field<CancellationTokenSource>(fixture, "_cancellation").Token;
    private static T Field<T>(Fixture fixture, string name) => (T)typeof(SceneQuickActionsRunner)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Runner)!;
    private static void SetField(Fixture fixture, string name, object value) => typeof(SceneQuickActionsRunner)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Runner, value);
    private static void MakeHotkeyEligible(Fixture fixture) => SetField(fixture, "_lastHotkeyAt", DateTime.MinValue);

    private sealed class SceneLogger : IPluginLogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public void Info(string message, IReadOnlyDictionary<string, object?>? details = null) { }
        public void Warning(string message, IReadOnlyDictionary<string, object?>? details = null) => Warnings.Enqueue(message);
        public void Error(string message, IReadOnlyDictionary<string, object?>? details = null) => Warnings.Enqueue(message);
    }
}
