using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using IDVBuff.PluginContracts;
using IDVBuff.Plugins.SceneQuickActions;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "10")]
[Trait("Issue", "12")]
public sealed partial class SceneQuickActionsActionTests
{
    private static readonly IntPtr GameWindow = new(101);
    private static readonly MatchHit OldHit = new(0.95, new Rect(10, 20, 10, 10), 1);
    private static readonly MatchHit NewHit = new(0.96, new Rect(50, 60, 10, 10), 1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThreeNewFramesWithoutPanelNeverUseOldQuickReplacePosition(bool useKey)
    {
        using var fixture = new Fixture();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2);
        fixture.AddFrame(3);
        fixture.AddFrame(4);

        fixture.Runner.ProcessOnce(PickupOptions(useKey), CancellationToken.None);

        Assert.Equal(4, fixture.Grabber.GrabCount);
        Assert.Empty(fixture.Input.Clicks);
        Assert.Single(fixture.Input.Bindings);
        Assert.Equal(SceneQuickActionsOptions.DefaultPickupAllVirtualKey, fixture.Input.Bindings.Single().VirtualKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedRecaptureNeverUsesOldQuickReplaceEvidence(bool useKey)
    {
        using var fixture = new Fixture();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);

        fixture.Runner.ProcessOnce(PickupOptions(useKey), CancellationToken.None);

        Assert.Equal(2, fixture.Grabber.GrabCount);
        Assert.Empty(fixture.Input.Clicks);
        Assert.Single(fixture.Input.Bindings);
    }

    [Fact]
    public void MouseQuickReplaceUsesHitAndBoundsFromSuccessfulNewFrame()
    {
        using var fixture = new Fixture();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2);
        fixture.AddFrame(3, quick: NewHit, bounds: new PluginClientBounds(200, 300, 200, 200));

        fixture.Runner.ProcessOnce(PickupOptions(), CancellationToken.None);

        Assert.Equal(3, fixture.Grabber.GrabCount);
        Assert.Equal((310, 430), Assert.Single(fixture.Input.Clicks));
        Assert.Single(fixture.Input.Bindings);
    }

    [Fact]
    public void MissingInitialQuickReplaceHitDoesNotCreateAnUnverifiedClick()
    {
        using var fixture = new Fixture();
        fixture.AddFrame(1, pickup: OldHit);
        fixture.AddFrame(2);
        fixture.AddFrame(3);
        fixture.AddFrame(4);

        fixture.Runner.ProcessOnce(PickupOptions(), CancellationToken.None);

        Assert.Empty(fixture.Input.Clicks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacementFrameFromAnotherGameWindowCannotContinueOriginalOperation(bool useKey)
    {
        using var fixture = new Fixture();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, pickup: NewHit, quick: NewHit, window: new IntPtr(202));

        fixture.Runner.ProcessOnce(PickupOptions(useKey), CancellationToken.None);

        Assert.Equal(2, fixture.Grabber.GrabCount);
        Assert.Empty(fixture.Input.Clicks);
        Assert.Single(fixture.Input.Bindings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationAfterSuccessfulRecaptureCannotInjectReplacement(bool useKey)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, pickup: NewHit, quick: NewHit);
        fixture.Grabber.OnGrab = count => { if (count == 2) cancellation.Cancel(); };

        Assert.Throws<OperationCanceledException>(() =>
            fixture.Runner.ProcessOnce(PickupOptions(useKey), cancellation.Token));

        Assert.Empty(fixture.Input.Clicks);
        Assert.Single(fixture.Input.Bindings);
    }

    [Fact]
    public void ForegroundLossAfterMovingCannotClickTheReplacement()
    {
        using var fixture = new Fixture();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, quick: NewHit);
        fixture.Input.OnSmoothMove = () => fixture.Input.Foreground = IntPtr.Zero;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Runner.ProcessOnce(PickupOptions(), CancellationToken.None));

        Assert.Empty(fixture.Input.Clicks);
    }

    [Fact]
    public async Task HotkeyCancelsInviteAndWaitsForItsBackpackCleanupBeforeDragging()
    {
        using var fixture = new Fixture();
        using var wake = new BlockingPoint();
        fixture.AddFrame(1, accept: OldHit);
        fixture.AddFrame(2, accept: NewHit);
        fixture.Input.OnTab = index => { if (index == 1) wake.Block(); };
        fixture.Start(InviteOptions());
        try
        {
            Assert.True(wake.Entered.Wait(TimeSpan.FromSeconds(5)));
            fixture.Hotkeys.PressBag();
            var drop = fixture.DropTask;
            Assert.False(drop.IsCompleted);
            Assert.Equal(["tab"], fixture.Input.Events.ToArray());

            wake.Release.Set();
            await drop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(fixture.Input.Clicks);
            var events = fixture.Input.Events.ToArray();
            Assert.Equal(["tab", "tab", "tab"], events.Take(3));
            Assert.Equal(6, events.Count(value => value == "left-down"));
            Assert.Equal(6, events.Count(value => value == "left-up"));
            Assert.Equal("tab", events.Last());
            fixture.Runner.ProcessOnce(InviteOptions(), CancellationToken.None);
            Assert.Equal(2, fixture.Grabber.GrabCount);
            Assert.Equal((55, 65), Assert.Single(fixture.Input.Clicks));
        }
        finally
        {
            wake.Release.Set();
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task HotkeyCancelsPickupBeforeItsSecondAction()
    {
        using var fixture = new Fixture();
        using var pickup = new BlockingPoint();
        fixture.AddFrame(1, pickup: OldHit, quick: OldHit);
        fixture.AddFrame(2, quick: NewHit);
        fixture.Input.OnBinding = binding =>
        {
            if (binding.VirtualKey == SceneQuickActionsOptions.DefaultPickupAllVirtualKey)
                pickup.Block();
        };
        fixture.Start(PickupOptions());
        try
        {
            Assert.True(pickup.Entered.Wait(TimeSpan.FromSeconds(5)));
            fixture.Hotkeys.PressBag();
            pickup.Release.Set();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, fixture.Grabber.GrabCount);
            Assert.Empty(fixture.Input.Clicks);
            Assert.Single(fixture.Input.Bindings);
        }
        finally
        {
            pickup.Release.Set();
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task InviteDuringHeldDragDoesNotCaptureOrReleaseInputAndIsRecapturedAfterCleanup()
    {
        using var fixture = new Fixture();
        using var held = new BlockingPoint();
        fixture.AddFrame(1, accept: NewHit);
        fixture.Input.OnLeftDown = held.Block;
        fixture.Start(IdleOptions());
        try
        {
            fixture.Hotkeys.PressHotbar();
            Assert.True(held.Entered.Wait(TimeSpan.FromSeconds(5)));

            fixture.Runner.ProcessOnce(InviteOptions(), CancellationToken.None);
            Assert.Equal(0, fixture.Grabber.GrabCount);
            Assert.Equal(1, fixture.Input.Events.Count(value => value == "tab"));
            Assert.DoesNotContain("left-up", fixture.Input.Events);
            Assert.Empty(fixture.Input.Clicks);

            held.Release.Set();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.ProcessOnce(InviteOptions(), CancellationToken.None);

            Assert.Equal(1, fixture.Grabber.GrabCount);
            Assert.Single(fixture.Input.Clicks);
            var events = fixture.Input.Events.ToArray();
            var releaseIndex = Array.IndexOf(events, "left-up");
            var clickIndex = Array.IndexOf(events, "click");
            Assert.True(releaseIndex > 0 && clickIndex > releaseIndex);
            Assert.Equal("tab", events[releaseIndex + 1]);
        }
        finally
        {
            held.Release.Set();
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task StopAndRestartWaitForHeldDragReleaseAndBackpackCleanup()
    {
        using var fixture = new Fixture();
        using var held = new BlockingPoint();
        fixture.Input.OnLeftDown = held.Block;
        fixture.Start(IdleOptions());
        Task? stop = null;
        Task? restart = null;
        try
        {
            fixture.Hotkeys.PressHotbar();
            Assert.True(held.Entered.Wait(TimeSpan.FromSeconds(5)));
            stop = Task.Run(fixture.Runner.Stop);
            Assert.True(fixture.Hotkeys.Cleared.Wait(TimeSpan.FromSeconds(5)));
            using var restartEntered = new ManualResetEventSlim();
            restart = Task.Run(() => { restartEntered.Set(); fixture.Runner.Start(); });
            Assert.True(restartEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(stop.IsCompleted);
            Assert.False(restart.IsCompleted);
            Assert.DoesNotContain("left-up", fixture.Input.Events);

            held.Release.Set();
            await Task.WhenAll(stop, restart).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, fixture.Input.Events.Count(value => value == "left-down"));
            Assert.Equal(1, fixture.Input.Events.Count(value => value == "left-up"));
            Assert.Equal(2, fixture.Input.Events.Count(value => value == "tab"));
            var before = fixture.Input.Events.Count;
            fixture.Input.OnLeftDown = null;
            fixture.Hotkeys.PressHotbar();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("tab", fixture.Input.Events.ToArray()[before]);
            Assert.Equal(2, fixture.Input.Events.Count(value => value == "left-up"));
        }
        finally
        {
            held.Release.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
            if (restart is not null) await restart.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task ForegroundChangeWhileHotkeyWaitsPreventsItsOldWindowDrag()
    {
        using var fixture = new Fixture();
        using var wake = new BlockingPoint();
        fixture.AddFrame(1, accept: OldHit);
        fixture.Input.OnTab = index => { if (index == 1) wake.Block(); };
        fixture.Start(InviteOptions());
        try
        {
            Assert.True(wake.Entered.Wait(TimeSpan.FromSeconds(5)));
            fixture.Hotkeys.PressBag();
            fixture.Input.Foreground = new IntPtr(202);
            wake.Release.Set();
            await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["tab"], fixture.Input.Events.ToArray());
        }
        finally
        {
            wake.Release.Set();
            fixture.Runner.Stop();
        }
    }

    [Fact]
    public async Task DragFailureReleasesItsOwnMouseButtonAndRestoresBackpack()
    {
        using var fixture = new Fixture();
        fixture.Input.OnCursorMove = count =>
        {
            if (count == 2) throw new InvalidOperationException("Controlled movement failure after LeftDown.");
        };
        fixture.Start(IdleOptions());

        fixture.Hotkeys.PressHotbar();
        await fixture.DropTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fixture.Input.Events.Count(value => value == "left-down"));
        Assert.Equal(1, fixture.Input.Events.Count(value => value == "left-up"));
        Assert.Equal(2, fixture.Input.Events.Count(value => value == "tab"));
        Assert.Equal("tab", fixture.Input.Events.Last());
    }

    private static SceneQuickActionsOptions IdleOptions() => new()
    {
        InviteEnabled = false,
        PickupEnabled = false,
        PollIntervalMilliseconds = 5000,
        MinimumRandomDelayMilliseconds = 0,
        MaximumRandomDelayMilliseconds = 0,
        BetweenClicksMilliseconds = 0,
        WakeDelayMilliseconds = 0
    };

    private static SceneQuickActionsOptions InviteOptions() => IdleOptions() with { InviteEnabled = true };

    private static SceneQuickActionsOptions PickupOptions(bool useKey = false) => IdleOptions() with
    {
        PickupEnabled = true,
        QuickReplaceBinding = useKey ? PluginInputBinding.Keyboard(0x52) : new PluginInputBinding()
    };

    private sealed class BlockingPoint : IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void Block()
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The controlled action was not released.");
        }

        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        public RecordingInput Input { get; } = new();
        public RecordingHotkeys Hotkeys { get; } = new();
        public QueueGrabber Grabber { get; } = new();
        public FrameMatcher Matcher { get; } = new();
        public SceneQuickActionsRunner Runner { get; }
        public SceneLogger Logger { get; } = new();

        public Task DropTask => (Task)typeof(SceneQuickActionsRunner)
            .GetField("_hotkeyTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Runner)!;

        public Fixture() => Runner = new SceneQuickActionsRunner(Grabber, Matcher, Logger, Input);

        public void Start(SceneQuickActionsOptions options)
        {
            Runner.SetOptions(options);
            Runner.AttachHotkeys(Hotkeys, "scene-quick-actions");
            Runner.Start();
        }

        public void AddFrame(byte id, MatchHit? pickup = null, MatchHit? quick = null, MatchHit? accept = null,
            PluginClientBounds? bounds = null, IntPtr? window = null)
        {
            Matcher.Hits[(id, SceneQuickActionsTemplates.PickupAll)] = pickup;
            Matcher.Hits[(id, SceneQuickActionsTemplates.QuickReplace)] = quick;
            Matcher.Hits[(id, SceneQuickActionsTemplates.AcceptButton)] = accept;
            Grabber.Frames.Enqueue(new FrameSpec(id, bounds ?? new PluginClientBounds(0, 0, 100, 100),
                window ?? GameWindow));
        }

        public void Dispose() { Runner.Dispose(); Hotkeys.Cleared.Dispose(); }
    }

    private readonly record struct FrameSpec(byte Id, PluginClientBounds Bounds, IntPtr Window);

    private sealed class QueueGrabber : ISceneQuickActionsFrameGrabber
    {
        private int _grabCount;
        public ConcurrentQueue<FrameSpec> Frames { get; } = new();
        public int GrabCount => Volatile.Read(ref _grabCount);
        public bool HasFastPath => true;
        public Action<int>? OnGrab { get; set; }

        public bool TryGrab(out GrabbedFrame? frame, out string failure, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref _grabCount);
            OnGrab?.Invoke(count);
            failure = "No next controlled frame.";
            frame = Frames.TryDequeue(out var spec)
                ? new GrabbedFrame(new Mat(100, 100, MatType.CV_8UC1, new Scalar(spec.Id)), spec.Bounds, spec.Window)
                : null;
            return frame is not null;
        }
    }

    private sealed class FrameMatcher : ISceneQuickActionsMatcher
    {
        public Dictionary<(byte Id, string Template), MatchHit?> Hits { get; } = new();

        public bool TryMatch(Mat grayFrame, string templateName, double threshold, out MatchHit hit)
        {
            hit = default;
            if (!Hits.TryGetValue((grayFrame.At<byte>(0, 0), templateName), out var found) || found is null)
                return false;
            hit = found.Value;
            return true;
        }
    }

    private sealed class RecordingHotkeys : IPluginInputService
    {
        public event EventHandler<PluginInputEventArgs>? BindingInvoked;
        public ManualResetEventSlim Cleared { get; } = new();
        public void SetBinding(string pluginId, string key, PluginInputBinding binding) { }
        public void ClearBindings(string pluginId) => Cleared.Set();
        public bool IsBindingPressed(string pluginId, string key) => false;
        public void PressBag() => Press(SceneQuickActionsOptions.DropBagHotkeyKey);
        public void PressHotbar() => Press(SceneQuickActionsOptions.DropHotbarHotkeyKey);
        private void Press(string key) => BindingInvoked?.Invoke(this,
            new PluginInputEventArgs("scene-quick-actions", key, Stopwatch.GetTimestamp(), true));
    }

    private sealed class RecordingInput : ISceneQuickActionsInput
    {
        private int _tabCount;
        private int _cursorMoves;
        private (int X, int Y) _cursor;
        public IntPtr Foreground = GameWindow;
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<(int X, int Y)> Clicks { get; } = new();
        public ConcurrentQueue<PluginInputBinding> Bindings { get; } = new();
        public ConcurrentQueue<(int X, int Y)> DragStarts { get; } = new();
        public Action<int>? OnTab { get; set; }
        public Action? OnLeftDown { get; set; }
        public Action? OnLeftUp { get; set; }
        public Action? OnClick { get; set; }
        public Action? OnSmoothMove { get; set; }
        public Action<int>? OnCursorMove { get; set; }
        public Action<PluginInputBinding>? OnBinding { get; set; }
        public bool IsForegroundWindow(IntPtr window) => window == Foreground;
        public bool TryGetForegroundGameWindow(out IntPtr window) { window = Foreground; return window != IntPtr.Zero; }
        public bool TryGetClientBounds(IntPtr window, out PluginClientBounds bounds)
        { bounds = new PluginClientBounds(0, 0, 1920, 1080); return IsForegroundWindow(window); }
        public void MoveSmoothly(int x, int y, int durationMilliseconds, CancellationToken token)
        { token.ThrowIfCancellationRequested(); _cursor = (x, y); Events.Enqueue($"smooth:{x},{y}"); OnSmoothMove?.Invoke(); }
        public void MoveCursorTo(int x, int y)
        { _cursor = (x, y); Events.Enqueue($"move:{x},{y}"); OnCursorMove?.Invoke(Interlocked.Increment(ref _cursorMoves)); }
        public void ClickLeft(int holdMilliseconds) { Events.Enqueue("click"); Clicks.Enqueue(_cursor); OnClick?.Invoke(); }
        public void SetLeftButton(bool down)
        {
            Events.Enqueue(down ? "left-down" : "left-up");
            if (down) { DragStarts.Enqueue(_cursor); OnLeftDown?.Invoke(); }
            else OnLeftUp?.Invoke();
        }
        public void InjectKey(uint virtualKey, int holdMilliseconds)
        { Events.Enqueue("tab"); OnTab?.Invoke(Interlocked.Increment(ref _tabCount)); }
        public void InjectBinding(PluginInputBinding binding, int holdMilliseconds)
        { Bindings.Enqueue(binding); Events.Enqueue("binding"); OnBinding?.Invoke(binding); }
    }
}
