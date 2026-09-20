using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenCvSharp;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Display;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using XamlWindow = Microsoft.UI.Xaml.Window;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;
/// <summary>Interactive frozen-game selector used only while F4 manual recognition is active.</summary>
public sealed partial class MapManualRecognitionWindow
{

    private static Rect CreateRect(Point start, Point end) => new(
        Math.Min(start.X, end.X),
        Math.Min(start.Y, end.Y),
        Math.Abs(end.X - start.X),
        Math.Abs(end.Y - start.Y));

    internal static async Task<BitmapImage> CreateBitmapAsync(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static RectInt32 ToRectInt32(MapScreenRect rect) => new(
        (int)Math.Round(rect.X),
        (int)Math.Round(rect.Y),
        (int)Math.Round(rect.Width),
        (int)Math.Round(rect.Height));

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
