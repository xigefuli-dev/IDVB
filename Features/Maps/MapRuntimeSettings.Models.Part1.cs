using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;
/// <summary>Persisted runtime configuration for the 解锁地图 status module.</summary>
public sealed partial class MapRuntimeSettings
{

    public void Normalize()
    {
        var previousSchema = SchemaVersion;
        SchemaVersion = CurrentSchemaVersion;
        if (SelectedMapId == Guid.Empty)
            SelectedMapId = null;
        LastSelectedMapClass = string.IsNullOrWhiteSpace(LastSelectedMapClass)
            ? null
            : LastSelectedMapClass.Trim();
        SelectedResolutionPreset = string.IsNullOrWhiteSpace(SelectedResolutionPreset)
            ? null
            : SelectedResolutionPreset.Trim();
        if (!Enum.IsDefined(FirstScanStrategy))
            FirstScanStrategy = FirstScanStrategy.DoubleGate;
        QuickScanBinding ??= new MapInputBinding();
        OverlayToggleBinding ??= new MapInputBinding();
        GameMapToggleBinding ??= new MapInputBinding();
        ControlPanelToggleBinding ??= new MapInputBinding();
        ManualRecognitionBinding ??= new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = (uint)Windows.System.VirtualKey.F4
        };
        SwitchFloorBinding ??= new MapInputBinding();
        TraditionalWindowSwitchFloorBinding ??= new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = (uint)Windows.System.VirtualKey.X
        };
        SaveMapCacheBinding ??= new MapInputBinding();
        RecognitionTuning ??= new MapRecognitionTuning();
        StructureRegistrationTuning ??= new MapStructureRegistrationTuning();
        SessionTuning ??= new MapSessionTuning();
        AlignmentCalibrations ??= [];
        FloorScaleCalibrations ??= [];
        NormalizeBinding(QuickScanBinding);
        NormalizeBinding(OverlayToggleBinding);
        NormalizeBinding(GameMapToggleBinding);
        NormalizeBinding(ControlPanelToggleBinding);
        NormalizeBinding(ManualRecognitionBinding);
        NormalizeBinding(SwitchFloorBinding);
        NormalizeBinding(TraditionalWindowSwitchFloorBinding);
        NormalizeBinding(SaveMapCacheBinding);
        if (QuickScanBinding.IsConfigured
            && QuickScanBinding.Equals(OverlayToggleBinding))
        {
            OverlayToggleBinding = new MapInputBinding();
        }
        if (ManualRecognitionBinding.IsConfigured
            && (ManualRecognitionBinding.Equals(QuickScanBinding)
                || ManualRecognitionBinding.Equals(OverlayToggleBinding)))
        {
            ManualRecognitionBinding = new MapInputBinding();
        }
        if (GameMapToggleBinding.IsConfigured
            && (GameMapToggleBinding.Equals(QuickScanBinding)
                || GameMapToggleBinding.Equals(OverlayToggleBinding)
                || GameMapToggleBinding.Equals(ManualRecognitionBinding)))
        {
            GameMapToggleBinding = new MapInputBinding();
        }
        if (ControlPanelToggleBinding.IsConfigured
            && (ControlPanelToggleBinding.Equals(QuickScanBinding)
                || ControlPanelToggleBinding.Equals(OverlayToggleBinding)
                || ControlPanelToggleBinding.Equals(ManualRecognitionBinding)
                || ControlPanelToggleBinding.Equals(GameMapToggleBinding)))
        {
            ControlPanelToggleBinding = new MapInputBinding();
        }
        if (SwitchFloorBinding.IsConfigured
            && (SwitchFloorBinding.Equals(QuickScanBinding)
                || SwitchFloorBinding.Equals(OverlayToggleBinding)
                || SwitchFloorBinding.Equals(ManualRecognitionBinding)
                || SwitchFloorBinding.Equals(GameMapToggleBinding)
                || SwitchFloorBinding.Equals(ControlPanelToggleBinding)))
        {
            SwitchFloorBinding = new MapInputBinding();
        }
        if (SaveMapCacheBinding.IsConfigured
            && (SaveMapCacheBinding.Equals(QuickScanBinding)
                || SaveMapCacheBinding.Equals(OverlayToggleBinding)
                || SaveMapCacheBinding.Equals(ManualRecognitionBinding)
                || SaveMapCacheBinding.Equals(GameMapToggleBinding)
                || SaveMapCacheBinding.Equals(ControlPanelToggleBinding)
                || SaveMapCacheBinding.Equals(SwitchFloorBinding)))
        {
            SaveMapCacheBinding = new MapInputBinding();
        }
        RecognitionTuning.Normalize();
        StructureRegistrationTuning.Normalize();
        SessionTuning.Normalize();
        FloorRecognitionTuning ??= new MapFloorRecognitionTuning();
        PlayerTrackingTuning ??= new MapPlayerTrackingTuning();
        FloorRecognitionTuning.Normalize();
        PlayerTrackingTuning.Normalize();
        AlignmentCalibrations = AlignmentCalibrations
            .Where(calibration => calibration?.IsValid is true)
            .GroupBy(calibration => new
            {
                calibration.MapId,
                calibration.Floor,
                calibration.ClientWidth,
                calibration.ClientHeight,
                calibration.ViewportWidth,
                calibration.ViewportHeight
            })
            .Select(group => group
                .OrderByDescending(calibration => calibration.UpdatedAt)
                .First()
                .Clone())
            .ToList();
        foreach (var calibration in FloorScaleCalibrations)
            calibration?.Normalize();
        FloorScaleCalibrations = FloorScaleCalibrations
            .Where(calibration => calibration?.IsValid is true)
            .GroupBy(calibration => new
            {
                calibration.MapId,
                calibration.MapUpdatedAt,
                calibration.PrimaryFloorKey,
                calibration.FloorKey
            })
            .Select(group => group
                .OrderByDescending(calibration => calibration.UpdatedAt)
                .First()
                .Clone())
            .ToList();
        // 首次运行或升级时注入默认分辨率调优档案
        ResolutionTuningProfiles ??= [];
        if (ResolutionTuningProfiles.Count == 0)
        {
            ResolutionTuningProfiles.AddRange(
            [
                new ResolutionTuningProfile
                {
                    Name = "2560×1600 @ 120 DPI",
                    ClientWidth = 2560, ClientHeight = 1600, Dpi = 120,
                    MinimumEdgeCoverage = 0.55d,
                    MinimumCandidateMargin = 0.08d,
                    VectorErrorTolerance = 0.04d
                },
                new ResolutionTuningProfile
                {
                    Name = "1920×1080 @ 120 DPI",
                    ClientWidth = 1920, ClientHeight = 1080, Dpi = 120,
                    MaximumChamferPixels = 3.0d,
                    MinimumEdgeCoverage = 0.30d,
                    EdgeDistanceTolerancePixels = 3.5d,
                    FastCoarseMaxDimension = 180,
                    FastCoarseDownsampleFactor = 2,
                    ScaleSearchRadius = 0.04d,
                    MinimumCandidateMargin = 0.03d
                },
                new ResolutionTuningProfile
                {
                    Name = "2560×1440 @ 120 DPI",
                    ClientWidth = 2560, ClientHeight = 1440, Dpi = 120,
                    MaximumChamferPixels = 3.0d,
                    FastCoarseMaxDimension = 160
                }
            ]);
        }

        // Session alignment is deliberately restricted to one uniform scale.
        OverlayAlignmentMode = MapOverlayAlignmentMode.Uniform;
        if (previousSchema < 3)
            RecognitionTuning.ForceBestRecognitionResult = false;
        MapViewportRegion = NormalizeRegion(MapViewportRegion);
        FloorDisplayRegion = NormalizeRegion(FloorDisplayRegion);
        CalibrationClientWidth = Math.Max(0, CalibrationClientWidth);
        CalibrationClientHeight = Math.Max(0, CalibrationClientHeight);
        CalibrationVersion = Math.Max(0, CalibrationVersion);
        if (MapViewportRegion is null)
        {
            CalibrationClientWidth = 0;
            CalibrationClientHeight = 0;
            CalibrationVersion = 0;
        }
        FloorCalibrationClientWidth = Math.Max(0, FloorCalibrationClientWidth);
        FloorCalibrationClientHeight = Math.Max(0, FloorCalibrationClientHeight);
        FloorCalibrationVersion = Math.Max(0, FloorCalibrationVersion);
        if (FloorDisplayRegion is null)
        {
            FloorCalibrationClientWidth = 0;
            FloorCalibrationClientHeight = 0;
            FloorCalibrationVersion = 0;
        }
        NormalizeDisplayCalibrationProfiles();
        if (previousSchema < 14)
        {
            StatusOffsetX = LegacyOffsetToNormalized(StatusOffsetX);
            StatusOffsetY = LegacyOffsetToNormalized(StatusOffsetY);
            MiniMapOffsetX = LegacyOffsetToNormalized(MiniMapOffsetX);
            MiniMapOffsetY = LegacyOffsetToNormalized(MiniMapOffsetY);
        }
        MiniMapScale = double.IsFinite(MiniMapScale)
            ? Math.Clamp(MiniMapScale, 0d, 1.0d)
            : 0.25d;
        MapOpacity = double.IsFinite(MapOpacity)
            ? Math.Clamp(MapOpacity, 0d, 1.0d) : 0.46d;
        StatusOpacity = double.IsFinite(StatusOpacity)
            ? Math.Clamp(StatusOpacity, 0d, 1.0d) : 1.0d;
        StatusScale = double.IsFinite(StatusScale)
            ? Math.Clamp(StatusScale, 0d, 1.0d) : 1.0d;
        StatusOffsetX = NormalizeRatio(StatusOffsetX);
        StatusOffsetY = NormalizeRatio(StatusOffsetY);
        MiniMapOpacity = double.IsFinite(MiniMapOpacity)
            ? Math.Clamp(MiniMapOpacity, 0d, 1.0d) : 0.55d;
        MiniMapOffsetX = NormalizeRatio(MiniMapOffsetX);
        MiniMapOffsetY = NormalizeRatio(MiniMapOffsetY);
    }

    private static double LegacyOffsetToNormalized(double value) =>
        double.IsFinite(value) ? Math.Clamp(value / 500d, 0d, 1d) : 0d;

    private static double NormalizeRatio(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;

    private static void NormalizeBinding(MapInputBinding binding)
    {
        if (binding.Kind == MapInputBindingKind.Keyboard && binding.VirtualKey == 0)
            binding.Kind = MapInputBindingKind.None;
        if (binding.Kind == MapInputBindingKind.Keyboard)
            binding.Modifiers &= MapInputModifiers.Control
                | MapInputModifiers.Alt
                | MapInputModifiers.Shift
                | MapInputModifiers.Windows;
        if (binding.Kind == MapInputBindingKind.Mouse && !Enum.IsDefined(binding.MouseButton))
            binding.Kind = MapInputBindingKind.None;
        if (binding.Kind != MapInputBindingKind.Keyboard)
            binding.Modifiers = MapInputModifiers.None;
        if (!Enum.IsDefined(binding.Kind))
            binding.Kind = MapInputBindingKind.None;
    }

    private static NormalizedRectangle? NormalizeRegion(NormalizedRectangle? region)
    {
        if (region?.IsValid is not true)
            return null;
        var left = Math.Clamp(region.X, 0d, 1d);
        var top = Math.Clamp(region.Y, 0d, 1d);
        var right = Math.Clamp(region.X + region.Width, left, 1d);
        var bottom = Math.Clamp(region.Y + region.Height, top, 1d);
        var normalized = new NormalizedRectangle
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        return normalized.IsValid ? normalized : null;
    }
}
