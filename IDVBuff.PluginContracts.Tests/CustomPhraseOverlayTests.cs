using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.Versioning;
using IDVBuff.PluginContracts;
using IDVBuff.Plugins.CustomPhrases;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

[SupportedOSPlatform("windows")]
public sealed class CustomPhraseOverlayTests
{
    private static readonly Type OverlayType = typeof(CustomPhrasePlugin).Assembly
        .GetType("IDVBuff.Plugins.CustomPhrases.CustomPhraseOverlay", throwOnError: true)!;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Theory]
    [InlineData(640, 360, 5)]
    [InlineData(640, 360, 30)]
    [InlineData(640, 240, 5)]
    [InlineData(640, 64, 30)]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 30)]
    public void Layout_FitsClientHeightAndKeepsColumnOrder(int width, int height, int count)
    {
        var client = new PluginClientBounds(-200, 70, width, height);
        var boxes = CreateBoxes(client, count);
        Assert.Equal(count, boxes.Length);
        for (var index = 0; index < count; index++)
        {
            Assert.True(boxes[index].Width > 0 && boxes[index].Height > 0);
            Assert.InRange(boxes[index].Top, client.Y, client.Y + height);
            Assert.InRange(boxes[index].Bottom, client.Y, client.Y + height);
            if (index % 5 != 0)
            {
                Assert.Equal(boxes[index - 1].Left, boxes[index].Left);
                Assert.True(boxes[index].Top >= boxes[index - 1].Bottom);
            }
        }
        if (height >= 360)
            Assert.All(boxes, box => Assert.Equal(56, box.Height));
    }

    [Fact]
    public void Layout_NormalWindowPreservesDimensionsAndSeventyPercentAnchor()
    {
        var boxes = CreateBoxes(new PluginClientBounds(0, 0, 1920, 1080), 6);
        Assert.Equal(new Rectangle(694, 592, 260, 56), boxes[0]);
        Assert.Equal(new Rectangle(694, 864, 260, 56), boxes[4]);
        Assert.Equal(new Rectangle(966, 592, 260, 56), boxes[5]);
    }

    [Fact]
    public void Wheel_AccumulatesHighResolutionDeltasAndPreservesRemainder()
    {
        var overlay = CreateOverlay();
        Assert.Equal(0, Selected(overlay));
        Scroll(overlay, -30, -30, -30);
        Assert.Equal(0, Selected(overlay));
        Scroll(overlay, -30);
        Assert.Equal(1, Selected(overlay));
        Scroll(overlay, -300);
        Assert.Equal(3, Selected(overlay));
        Scroll(overlay, -60);
        Assert.Equal(4, Selected(overlay));
        Scroll(overlay, 240);
        Assert.Equal(2, Selected(overlay));
    }

    [Fact]
    public void Wheel_ReversalCancelsPartialDeltaAndClampsAtEdges()
    {
        var overlay = CreateOverlay();
        Scroll(overlay, -60, 60, 0);
        Assert.Equal(0, Selected(overlay));
        Scroll(overlay, -120);
        Assert.Equal(1, Selected(overlay));
        Scroll(overlay, short.MinValue);
        Assert.Equal(9, Selected(overlay));
        Scroll(overlay, short.MaxValue);
        Assert.Equal(0, Selected(overlay));
        Assert.Equal(119, Get<int>(overlay, "_wheelDeltaRemainder"));
        Scroll(overlay, -120);
        Assert.Equal(0, Selected(overlay));
        Scroll(overlay, -119);
        Assert.Equal(1, Selected(overlay));
    }

    [Fact]
    public void Wheel_HideAndReopenResetRemainderAndHiddenInputIsIgnored()
    {
        var overlay = CreateOverlay();
        Scroll(overlay, -180);
        Assert.Equal(1, (int)Invoke(overlay, "Hide")!);
        Assert.Equal(0, Get<int>(overlay, "_wheelDeltaRemainder"));
        Scroll(overlay, -120);
        Assert.Equal(1, Selected(overlay));
        Invoke(overlay, "ResetSelectionToFirst");
        Set(overlay, "_visible", true);
        Scroll(overlay, -60);
        Assert.Equal(0, Selected(overlay));
        Invoke(overlay, "ResetSelectionToFirst");
        Scroll(overlay, -60);
        Assert.Equal(0, Selected(overlay));
        Scroll(overlay, -60);
        Assert.Equal(1, Selected(overlay));
        Invoke(overlay, "Dispose");
        Assert.Equal(0, Get<int>(overlay, "_wheelDeltaRemainder"));
    }

    [Fact]
    public void Wheel_EmptyMenuHasNoSelectionOrPendingDelta()
    {
        var overlay = CreateOverlay();
        Set(overlay, "_phrases", Array.Empty<string>());
        Invoke(overlay, "ResetSelectionToFirst");
        Scroll(overlay, -240);
        Assert.Equal(-1, Selected(overlay));
        Assert.Equal(0, Get<int>(overlay, "_wheelDeltaRemainder"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(0x100, true)]
    [InlineData(0x101, false)]
    public void RawInput_ForegroundCallsDefaultProcedureAndSinkReturnsZero(int code, bool cleanup)
    {
        var procedure = OverlayType.GetMethods(PrivateStatic)
            .Single(method => method.Name == "WindowProcedureCore" && method.GetParameters().Length == 5);
        var calls = 0;
        IntPtr Default(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            calls++;
            Assert.Equal(new IntPtr(42), window);
            Assert.Equal(0x00FFu, message);
            Assert.Equal(new IntPtr(code), wParam);
            Assert.Equal(IntPtr.Zero, lParam);
            return new IntPtr(123);
        }
        Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> callback = Default;
        var nativeCallback = Delegate.CreateDelegate(procedure.GetParameters()[4].ParameterType,
            callback.Target, callback.Method);
        var overlays = (IDictionary)OverlayType.GetField("Overlays", PrivateStatic)!.GetValue(null)!;
        overlays.Add(new IntPtr(42), CreateOverlay());
        try
        {
            var result = procedure.Invoke(null, [new IntPtr(42), 0x00FFu, new IntPtr(code), IntPtr.Zero, nativeCallback]);
            Assert.Equal(IntPtr.Zero, result);
            Assert.Equal(cleanup ? 1 : 0, calls);
        }
        finally
        {
            overlays.Remove(new IntPtr(42));
        }
    }

    private static Rectangle[] CreateBoxes(PluginClientBounds client, int count)
    {
        var phrases = Enumerable.Range(0, count).Select(index => $"Phrase {index}").ToArray();
        var boxes = (IEnumerable)OverlayType.GetMethod("CreateBoxes", PrivateStatic)!
            .Invoke(null, [phrases, client])!;
        return boxes.Cast<object>().Select(box => (Rectangle)box.GetType()
            .GetProperty("Bounds")!.GetValue(box)!).ToArray();
    }

    private static object CreateOverlay()
    {
        var overlay = Activator.CreateInstance(OverlayType, nonPublic: true)!;
        Set(overlay, "_phrases", Enumerable.Range(0, 10).Select(index => $"Phrase {index}").ToArray());
        Set(overlay, "_visible", true);
        Invoke(overlay, "ResetSelectionToFirst");
        return overlay;
    }

    private static int Selected(object overlay) => Get<int>(overlay, "_selectedIndex");
    private static T Get<T>(object overlay, string field) =>
        (T)OverlayType.GetField(field, PrivateInstance)!.GetValue(overlay)!;
    private static void Set(object overlay, string field, object value) =>
        OverlayType.GetField(field, PrivateInstance)!.SetValue(overlay, value);
    private static object? Invoke(object overlay, string method, params object[] arguments) =>
        OverlayType.GetMethod(method, PrivateInstance)!.Invoke(overlay, arguments);
    private static void Scroll(object overlay, params int[] deltas)
    {
        foreach (var delta in deltas)
            Invoke(overlay, "ScrollSelection", delta);
    }
}
