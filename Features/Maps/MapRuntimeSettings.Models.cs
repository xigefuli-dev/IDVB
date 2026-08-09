using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>Persisted runtime configuration for the 解锁地图 status module.</summary>
public sealed partial class MapRuntimeSettings
{
    public const int CurrentSchemaVersion = 10;
    public const int CurrentCalibrationVersion = MapRuntimeSettingsRules.CurrentCalibrationVersion;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool IsEnabled { get; set; }
    /// <summary>首次扫描策略：默认双门对齐，可切换为侧门扫描。</summary>
    public FirstScanStrategy FirstScanStrategy { get; set; } = FirstScanStrategy.SideEntrance;
    /// <summary>
    /// The stable identity chosen by quick scan or confirmed manual recognition.
    /// Runtime alignment state is deliberately not persisted.
    /// </summary>
    public Guid? SelectedMapId { get; set; }
    public bool ShowOverlayStatus { get; set; } = true;
    public bool CollectLogs { get; set; }
    public bool CollectAlignmentResearchData { get; set; }
    /// <summary>
    /// 设为 true 则不运行楼层图像识别，使用用户手动切换的楼层；
    /// 尚未手动切换时使用地图主层。
    /// </summary>
    public bool SkipFloorRecognition { get; set; }
    public bool AllowMapExtendBeyondBounds { get; set; }
    public bool PersistentMiniMapEnabled { get; set; }
    public double MiniMapScale { get; set; } = 0.25d;
    public bool PlayerTrackingEnabled { get; set; } = false;
    public bool AllowAutomaticMapCache { get; set; }
    public bool ReverseAlternateDisplay { get; set; }
    public bool ShowGateMarkers { get; set; } = true;
    public bool ShowAuxiliaryAnchors { get; set; } = true;
    public bool ShowTextAnnotations { get; set; } = true;
    public bool ShowBoxAnnotations { get; set; } = true;
    public bool ShowGateMarkersOnMiniMap { get; set; } = true;
    public bool ShowAuxiliaryAnchorsOnMiniMap { get; set; } = true;
    public bool ShowTextAnnotationsOnMiniMap { get; set; } = true;
    public bool ShowBoxAnnotationsOnMiniMap { get; set; } = true;
    public bool ShowFloorOnMiniMap { get; set; } = true;
    public double StatusOpacity { get; set; } = 1.0d;
    public double StatusOffsetX { get; set; }
    public double StatusOffsetY { get; set; }
    public double MiniMapOpacity { get; set; } = 0.55d;
    public double MiniMapOffsetX { get; set; }
    public double MiniMapOffsetY { get; set; } = 50d;
    public MapOverlayAlignmentMode OverlayAlignmentMode { get; set; } = MapOverlayAlignmentMode.Uniform;
    public MapInputBinding QuickScanBinding { get; set; } = new();
    public MapInputBinding OverlayToggleBinding { get; set; } = new();
    public MapInputBinding GameMapToggleBinding { get; set; } = new();
    public MapInputBinding ControlPanelToggleBinding { get; set; } = new();
    public MapInputBinding ManualRecognitionBinding { get; set; } = new();
    public MapInputBinding SwitchFloorBinding { get; set; } = new();
    public MapInputBinding SaveMapCacheBinding { get; set; } = new();
    public MapRecognitionTuning RecognitionTuning { get; set; } = new();
    public MapStructureRegistrationTuning StructureRegistrationTuning { get; set; } = new();
    public MapSessionTuning SessionTuning { get; set; } = new();
    public MapFloorRecognitionTuning FloorRecognitionTuning { get; set; } = new();
    public MapPlayerTrackingTuning PlayerTrackingTuning { get; set; } = new();
    public List<MapAlignmentCalibration> AlignmentCalibrations { get; set; } = [];
    public List<MapFloorScaleCalibration> FloorScaleCalibrations { get; set; } = [];
    public List<ResolutionTuningProfile> ResolutionTuningProfiles { get; set; } = [];
    public NormalizedRectangle? MapViewportRegion { get; set; }
    public int CalibrationClientWidth { get; set; }
    public int CalibrationClientHeight { get; set; }
    public int CalibrationVersion { get; set; }
    public NormalizedRectangle? FloorDisplayRegion { get; set; }
    public int FloorCalibrationClientWidth { get; set; }
    public int FloorCalibrationClientHeight { get; set; }
    public int FloorCalibrationVersion { get; set; }

    /// <summary>
    /// Creates the safe, first-run baseline for a public release.
    /// Users opt in to the map module, choose their own bindings, and collect
    /// their own calibration data on the first run.
    /// </summary>
    public static MapRuntimeSettings CreateDefault() => new()
    {
        IsEnabled = false,
        FirstScanStrategy = FirstScanStrategy.SideEntrance,
        ShowOverlayStatus = true,
        CollectLogs = false,
        CollectAlignmentResearchData = false,
        SkipFloorRecognition = false,
        AllowMapExtendBeyondBounds = false,
        PersistentMiniMapEnabled = false,
        MiniMapScale = 0.25d,
        PlayerTrackingEnabled = false,
        AllowAutomaticMapCache = false,
        ReverseAlternateDisplay = false,
        ShowGateMarkers = true,
        ShowAuxiliaryAnchors = true,
        ShowTextAnnotations = true,
        ShowBoxAnnotations = true,
        ShowGateMarkersOnMiniMap = true,
        ShowAuxiliaryAnchorsOnMiniMap = true,
        ShowTextAnnotationsOnMiniMap = true,
        ShowBoxAnnotationsOnMiniMap = true,
        ShowFloorOnMiniMap = true,
        StatusOpacity = 1.0d,
        StatusOffsetX = 0d,
        StatusOffsetY = 0d,
        MiniMapOpacity = 0.55d,
        MiniMapOffsetX = 0d,
        MiniMapOffsetY = 50d,
        OverlayAlignmentMode = MapOverlayAlignmentMode.Uniform,
        QuickScanBinding = new MapInputBinding(),
        OverlayToggleBinding = new MapInputBinding(),
        GameMapToggleBinding = new MapInputBinding(),
        ControlPanelToggleBinding = new MapInputBinding(),
        ManualRecognitionBinding = new MapInputBinding(),
        SwitchFloorBinding = new MapInputBinding(),
        SaveMapCacheBinding = new MapInputBinding(),
        RecognitionTuning = new MapRecognitionTuning
        {
            GateTemplateThreshold = 0.72d,
            MinimumConfidence = 0.50d,
            VectorErrorTolerance = 0.15d,
            AmbiguityMargin = 0.015d,
            ConfirmationAdvantage = 0.08d,
            ForceBestRecognitionResult = false,
            ForceCandidateSelection = true,
            WarmGateSearchBudgetMs = 120,
            ConfirmationGateSearchBudgetMs = 0,
            ConfirmationRoiTemplatePaddingFactor = 1d,
            ConfirmationRoiMinimumPaddingPixels = 24,
            ConfirmationMaximumMapDragPixelsPerSecond = 600d,
            ConfirmationSchedulingSlackMilliseconds = 100,
            SideEntranceFeatureRadius = 80
        },
        StructureRegistrationTuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = MapStructureRegistrationTuning.CurrentSchemaVersion,
            UseAuxiliaryAnchorRecognition = false,
            ReusePreviousAlignmentResult = true,
            MaximumAuxiliaryTemplates = 4,
            AuxiliaryDirectLockConfidence = 0.82d,
            StructureFallbackBudgetMilliseconds = 1500,
            PreviousAlignmentSearchRadiusPixels = 96,
            TrackingSearchRadiusPixels = 48,
            TrackingScaleSearchRadius = 0.005d,
            EarlyTerminationScoreThreshold = 0.55d,
            SkipEccScoreThreshold = 8d,
            MinimumEdgePixels = 90,
            MinimumSpanPixels = 28,
            MinimumConsistentPartitions = 2,
            TopCandidateCount = 6,
            MaximumChamferPixels = 3.2d,
            MinimumEdgeCoverage = 0.40d,
            MinimumOccupancyCoverage = 0.42d,
            MinimumCandidateMargin = 0.04d,
            LocalSearchRadiusRatio = 0.20d,
            ScaleSearchRadius = 0.02d,
            ScaleSearchStep = 0.01d,
            EdgeDistanceTolerancePixels = 2.25d,
            DistanceClipPixels = 12d,
            EnableDebugOutput = false,
            EnableEccRefinement = false,
            EnableFeatureVoting = true,
            MaximumTranslationCandidates = 5,
            FeatureRatioThreshold = 0.64d,
            FeatureInlierTolerancePixels = 6d,
            MaximumPlayerPriorDistanceRatio = 0.45d,
            MapViewportEdgeMargin = 0.20d,
            EnableVisibleMask = false,
            EnableVisibleAwareShadow = false,
            EnableVisibleAwareInjection = true,
            EnableVisibleAwareEarlyExit = true,
            VisibleAwareSearchBudgetMilliseconds = 150,
            VisibleAwareCoarseDownsample = 4,
            VisibleAwareTopK = 5,
            VisibleAwareMinimumVisibleFraction = 0.05d,
            VisibleAwareMinimumVisibleStructurePixels = 50,
            SafeVisibleMaskErodePixels = 1,
            VisibleVMin = 42,
            VisibleSMin = 14,
            VisibleHighlightVMin = 80,
            VisibleAwareEarlyTerminationMaxCompositeCost = 0.55d,
            EnableFastAlignment = true,
            FastFallbackToLegacy = true,
            FastAlignmentShadowMode = false,
            FastCoarseDownsampleFactor = 2,
            FastCoarseTopK = 5,
            FastCoarseNmsRadius = 12,
            FastCoarseMaxDimension = 200
        },
        SessionTuning = new MapSessionTuning
        {
            SchemaVersion = MapSessionTuning.CurrentSchemaVersion,
            OpeningAnimationDelayMilliseconds = 10,
            OpeningTimeoutMilliseconds = 3000,
            StableFrameIntervalMilliseconds = 20,
            StableFrameCount = 2,
            StableFrameDifference = 0.015d,
            PresencePollingMilliseconds = 200,
            PlayerPollingMilliseconds = 100,
            WindowValidationMilliseconds = 500,
            HighConfidence = 0.70d,
            MediumConfidence = 0.60d,
            MediumConfidenceFrames = 2,
            CandidateStabilityPixels = 3d,
            NativeScaleChangeRatio = 0.03d,
            SkipStabilityConfirmation = true,
            ViewportIgnoreRegions = []
        },
        FloorRecognitionTuning = new MapFloorRecognitionTuning
        {
            MinimumConfidence = 0.60d,
            MinimumLocalizationConfidence = 0.70d,
            MaximumRecognitionWindowMilliseconds = 3000,
            FirstFloorConfirmationFrames = 2,
            SecondFloorConfirmationFrames = 3
        },
        PlayerTrackingTuning = new MapPlayerTrackingTuning
        {
            MinimumConfidence = 0.70d,
            LocalSearchFailureLimit = 5,
            StaleHideMilliseconds = 500
        }
    };

    [JsonIgnore]
    public bool IsMapViewportCalibrated => MapRuntimeSettingsRules.IsCalibrationCurrent(
        MapViewportRegion?.IsValid is true,
        CalibrationClientWidth,
        CalibrationClientHeight,
        CalibrationVersion);

    [JsonIgnore]
    public bool IsFloorDisplayCalibrated => MapRuntimeSettingsRules.IsCalibrationCurrent(
        FloorDisplayRegion?.IsValid is true,
        FloorCalibrationClientWidth,
        FloorCalibrationClientHeight,
        FloorCalibrationVersion);

    public MapRuntimeSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        IsEnabled = IsEnabled,
        FirstScanStrategy = FirstScanStrategy,
        SelectedMapId = SelectedMapId,
        ShowOverlayStatus = ShowOverlayStatus,
        CollectLogs = CollectLogs,
        CollectAlignmentResearchData = CollectAlignmentResearchData,
        SkipFloorRecognition = SkipFloorRecognition,
        OverlayAlignmentMode = OverlayAlignmentMode,
        QuickScanBinding = QuickScanBinding?.Clone() ?? new MapInputBinding(),
        OverlayToggleBinding = OverlayToggleBinding?.Clone() ?? new MapInputBinding(),
        GameMapToggleBinding = GameMapToggleBinding?.Clone() ?? new MapInputBinding(),
        ControlPanelToggleBinding =
            ControlPanelToggleBinding?.Clone() ?? new MapInputBinding(),
        ManualRecognitionBinding = ManualRecognitionBinding?.Clone() ?? new MapInputBinding(),
        SwitchFloorBinding = SwitchFloorBinding?.Clone() ?? new MapInputBinding(),
        SaveMapCacheBinding = SaveMapCacheBinding?.Clone() ?? new MapInputBinding(),
        AllowAutomaticMapCache = AllowAutomaticMapCache,
        RecognitionTuning = RecognitionTuning?.Clone() ?? new MapRecognitionTuning(),
        StructureRegistrationTuning =
            StructureRegistrationTuning?.Clone() ?? new MapStructureRegistrationTuning(),
        SessionTuning = SessionTuning?.Clone() ?? new MapSessionTuning(),
        FloorRecognitionTuning = FloorRecognitionTuning?.Clone()
            ?? new MapFloorRecognitionTuning(),
        PlayerTrackingTuning = PlayerTrackingTuning?.Clone()
            ?? new MapPlayerTrackingTuning(),
        AlignmentCalibrations = AlignmentCalibrations?
            .Where(calibration => calibration?.IsValid is true)
            .Select(calibration => calibration.Clone())
            .ToList() ?? [],
        FloorScaleCalibrations = FloorScaleCalibrations?
            .Where(calibration => calibration is not null)
            .Select(calibration => calibration.Clone())
            .ToList() ?? [],
        ResolutionTuningProfiles = ResolutionTuningProfiles?
            .Select(p => new ResolutionTuningProfile
            {
                Name = p.Name,
                ClientWidth = p.ClientWidth,
                ClientHeight = p.ClientHeight,
                Dpi = p.Dpi,
                MatchTolerancePixels = p.MatchTolerancePixels,
                MaximumChamferPixels = p.MaximumChamferPixels,
                MinimumEdgeCoverage = p.MinimumEdgeCoverage,
                MinimumOccupancyCoverage = p.MinimumOccupancyCoverage,
                EdgeDistanceTolerancePixels = p.EdgeDistanceTolerancePixels,
                FastCoarseMaxDimension = p.FastCoarseMaxDimension,
                FastCoarseDownsampleFactor = p.FastCoarseDownsampleFactor,
                ScaleSearchRadius = p.ScaleSearchRadius,
                ScaleSearchStep = p.ScaleSearchStep,
                MinimumCandidateMargin = p.MinimumCandidateMargin,
                GateTemplateThreshold = p.GateTemplateThreshold,
                VectorErrorTolerance = p.VectorErrorTolerance
            })
            .ToList() ?? [],
        DisplayCalibrationProfiles = DisplayCalibrationProfiles?
            .Where(profile => profile is not null)
            .Select(profile => profile.Clone())
            .ToList() ?? [],
        MapViewportRegion = MapViewportRegion?.Clone(),
        CalibrationClientWidth = CalibrationClientWidth,
        CalibrationClientHeight = CalibrationClientHeight,
        CalibrationVersion = CalibrationVersion,
        FloorDisplayRegion = FloorDisplayRegion?.Clone(),
        FloorCalibrationClientWidth = FloorCalibrationClientWidth,
        FloorCalibrationClientHeight = FloorCalibrationClientHeight,
        FloorCalibrationVersion = FloorCalibrationVersion,
        AllowMapExtendBeyondBounds = AllowMapExtendBeyondBounds,
        PersistentMiniMapEnabled = PersistentMiniMapEnabled,
        PlayerTrackingEnabled = PlayerTrackingEnabled,
        ReverseAlternateDisplay = ReverseAlternateDisplay,
        MiniMapScale = MiniMapScale,
        ShowGateMarkers = ShowGateMarkers,
        ShowAuxiliaryAnchors = ShowAuxiliaryAnchors,
        ShowTextAnnotations = ShowTextAnnotations,
        ShowBoxAnnotations = ShowBoxAnnotations,
        ShowGateMarkersOnMiniMap = ShowGateMarkersOnMiniMap,
        ShowAuxiliaryAnchorsOnMiniMap = ShowAuxiliaryAnchorsOnMiniMap,
        ShowTextAnnotationsOnMiniMap = ShowTextAnnotationsOnMiniMap,
        ShowBoxAnnotationsOnMiniMap = ShowBoxAnnotationsOnMiniMap,
        ShowFloorOnMiniMap = ShowFloorOnMiniMap,
        StatusOpacity = StatusOpacity,
        StatusOffsetX = StatusOffsetX,
        StatusOffsetY = StatusOffsetY,
        MiniMapOpacity = MiniMapOpacity,
        MiniMapOffsetX = MiniMapOffsetX,
        MiniMapOffsetY = MiniMapOffsetY
    };

    public void Normalize()
    {
        var previousSchema = SchemaVersion;
        SchemaVersion = CurrentSchemaVersion;
        if (SelectedMapId == Guid.Empty)
            SelectedMapId = null;
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
                    MaximumChamferPixels = 4.5d,
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
                    MaximumChamferPixels = 3.5d,
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
        MiniMapScale = double.IsFinite(MiniMapScale)
            ? Math.Clamp(MiniMapScale, 0.10d, 1.0d)
            : 0.25d;
        StatusOpacity = double.IsFinite(StatusOpacity)
            ? Math.Clamp(StatusOpacity, 0d, 1.0d) : 1.0d;
        StatusOffsetX = double.IsFinite(StatusOffsetX) ? StatusOffsetX : 0d;
        StatusOffsetY = double.IsFinite(StatusOffsetY) ? StatusOffsetY : 0d;
        MiniMapOpacity = double.IsFinite(MiniMapOpacity)
            ? Math.Clamp(MiniMapOpacity, 0d, 1.0d) : 0.55d;
        MiniMapOffsetX = double.IsFinite(MiniMapOffsetX) ? MiniMapOffsetX : 0d;
        MiniMapOffsetY = double.IsFinite(MiniMapOffsetY) ? MiniMapOffsetY : 50d;
    }

    private static void NormalizeBinding(MapInputBinding binding)
    {
        if (binding.Kind == MapInputBindingKind.Keyboard && binding.VirtualKey == 0)
            binding.Kind = MapInputBindingKind.None;
        if (binding.Kind == MapInputBindingKind.Mouse && !Enum.IsDefined(binding.MouseButton))
            binding.Kind = MapInputBindingKind.None;
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
