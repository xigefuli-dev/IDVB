using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public class IdvaBackgroundProtectionTests
{
    [Fact]
    public void RouteRepairPreservesBlackGapEvenAfterHoleFilling()
    {
        using var source = new Mat(160, 240, MatType.CV_8UC3, Scalar.Black);
        var roomColor = new Scalar(60, 73, 89);
        Cv2.Rectangle(source, new Rect(20, 20, 200, 120), roomColor, -1);
        // A genuine narrow enclosed void, and an equally narrow colored route.
        Cv2.Rectangle(source, new Rect(110, 40, 12, 80), Scalar.Black, -1);
        Cv2.Rectangle(source, new Rect(55, 20, 12, 120), new Scalar(0, 0, 255), -1);
        using var json = JsonDocument.Parse("""
            {"parameters":{"room_hsv_lo":[0,18,40],"room_hsv_hi":[25,165,200],
              "corridor_hsv_lo":[95,14,82],"corridor_hsv_hi":[130,105,200]},
             "pipeline":[{"stage":"color_classification","mode":"HSV_RANGE"},
              {"stage":"directional_bridge","horizontal_gap_px":17,"vertical_gap_px":17},
              {"stage":"fill_small_holes","room_max_hole_area":8000,"corridor_max_hole_area":4000},
              {"stage":"contours","retrieval":"RETR_LIST","chain":"CHAIN_APPROX_SIMPLE"},
              {"stage":"draw_edges","line_width_px":2,"antialias":false}]}
            """);
        var root = json.RootElement;
        var algorithm = new IdvaStructureAlgorithm("test", "test", "1.1", "", [],
            root.GetProperty("parameters"), root.GetProperty("pipeline").EnumerateArray().ToArray());
        using var line = new IdvaStructureLineEngine().Execute(algorithm, source);
        Assert.Equal(255, line.At<byte>(80, 109));
        Assert.Equal(255, line.At<byte>(80, 122));
        Assert.Equal(0, line.At<byte>(80, 60));
        Assert.Equal(0, line.At<byte>(80, 116));
    }
}
