using OpenCvSharp;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record IdvaStructureAlgorithm(
    string AlgorithmId,
    string DisplayName,
    string SchemaVersion,
    string Sha256,
    byte[] PackageBytes,
    JsonElement Parameters,
    IReadOnlyList<JsonElement> Pipeline);

public sealed record IdvaStageProgress(
    int StageIndex,
    int StageCount,
    double StageFraction,
    string StageName);

public sealed record PrebuiltStructureLineResult(
    int Width,
    int Height,
    long EdgePixels);

/// <summary>Loads and executes the restricted IDVA structure-map DSL.</summary>
public sealed partial class IdvaStructureLineEngine
{
    private const int MaximumPackageBytes = 1024 * 1024;
    private const int MaximumStages = 64;
    private static readonly HashSet<string> SupportedStages =
    [
        "tone_adjustment",
        "clahe_contrast",
        "unsharp_sharpen",
        "separate_classes",
        "wall_barrier_separation",
        "color_classification",
        "ignore_route_overlays",
        "class_conflict_resolution",
        "morph_open",
        "remove_small_components",
        "morph_close",
        "fill_holes",
        "directional_bridge",
        "fill_small_holes",
        "contours",
        "room_contours",
        "corridor_contours",
        "source_edge_evidence",
        "draw_edges"
    ];

    public async Task<IdvaStructureAlgorithm> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".idva", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .idva 算法包。");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException("IDVA 算法包不存在、为空或超过 1 MB。 ");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var root = document.RootElement;
        RequireString(root, "format", "IDVA");
        RequireString(root, "schema_version", "1.1");
        RequireString(root, "profile_family", "structure-map");
        ValidateRuntime(root);
        ValidateGeometry(root);
        ValidateInputOutput(root);
        if (root.TryGetProperty("reference_implementation", out _)
            || root.TryGetProperty("source", out _))
        {
            throw new InvalidDataException("IDVA 1.1 不允许携带或执行源代码。");
        }
        var algorithmId = RequireNonEmptyString(root, "algorithm_id");
        var displayName = RequireNonEmptyString(root, "display_name");
        var parameters = RequireObject(root, "parameters").Clone();
        var pipelineElement = RequireArray(root, "pipeline");
        if (pipelineElement.GetArrayLength() is <= 0 or > MaximumStages)
            throw new InvalidDataException("pipeline 阶段数必须在 1 到 64 之间。");
        var pipeline = pipelineElement.EnumerateArray().Select(stage => stage.Clone()).ToArray();
        foreach (var stage in pipeline)
        {
            if (stage.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("pipeline 阶段必须是对象。");
            var name = RequireNonEmptyString(stage, "stage");
            if (!SupportedStages.Contains(name))
                throw new InvalidDataException($"不支持的 IDVA 阶段：{name}。");
        }
        if (RequireNonEmptyString(pipeline[^1], "stage") != "draw_edges")
            throw new InvalidDataException("IDVA pipeline 必须以 draw_edges 结束。");
        var outputLineWidth = ReadBoundedInt(RequireObject(root, "output"), "line_width_px", 1, 16);
        if (ReadBoundedInt(pipeline[^1], "line_width_px", 1, 16) != outputLineWidth)
            throw new InvalidDataException("output 与 draw_edges 的线宽必须一致。");
        return new IdvaStructureAlgorithm(
            algorithmId,
            displayName,
            "1.1",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes,
            parameters,
            pipeline);
    }

    public PrebuiltStructureLineResult Execute(
        IdvaStructureAlgorithm algorithm,
        string inputPath,
        string outputPath,
        Action<IdvaStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        using var source = Cv2.ImRead(inputPath, ImreadModes.Unchanged);
        if (source.Empty())
            throw new InvalidDataException("无法解码裁剪后的楼层图像。");
        using var edges = Execute(algorithm, source, progress, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (!Cv2.ImWrite(outputPath, edges))
            throw new IOException("无法写入预制线图 PNG。");
        return new PrebuiltStructureLineResult(
            edges.Width,
            edges.Height,
            Cv2.CountNonZero(edges));
    }

    public Mat Execute(
        IdvaStructureAlgorithm algorithm,
        Mat source,
        Action<IdvaStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new InvalidDataException("IDVA 输入图像不能为空。");
        using var state = PipelineState.Create(source);
        for (var index = 0; index < algorithm.Pipeline.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stage = algorithm.Pipeline[index];
            var stageName = RequireNonEmptyString(stage, "stage");
            progress?.Invoke(new IdvaStageProgress(index, algorithm.Pipeline.Count, 0d, stageName));
            ExecuteStage(
                state,
                algorithm.Parameters,
                stage,
                fraction => progress?.Invoke(new IdvaStageProgress(
                    index,
                    algorithm.Pipeline.Count,
                    Math.Clamp(fraction, 0d, 1d),
                    stageName)),
                cancellationToken);
            progress?.Invoke(new IdvaStageProgress(index + 1, algorithm.Pipeline.Count, 0d, stageName));
        }
        if (state.Edges is null || state.Edges.Empty())
            throw new InvalidDataException("IDVA pipeline 没有生成线图输出。");
        if (state.Edges.Width != source.Width || state.Edges.Height != source.Height)
            throw new InvalidDataException("IDVA 输出违反 preserve_input_size 约束。");
        return state.Edges.Clone();
    }

    private static void ExecuteStage(
        PipelineState state,
        JsonElement parameters,
        JsonElement stage,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var name = RequireNonEmptyString(stage, "stage");
        switch (name)
        {
            case "tone_adjustment":
                ApplyToneAdjustment(state, stage);
                break;
            case "clahe_contrast":
                ApplyClaheContrast(state, stage);
                break;
            case "unsharp_sharpen":
                ApplyUnsharpMask(state, stage);
                break;
            case "separate_classes":
                ApplySeparateClasses(state, stage);
                break;
            case "wall_barrier_separation":
                ApplyWallBarriers(state, stage);
                break;
            case "color_classification":
                Classify(state, parameters, stage, progress, cancellationToken);
                break;
            case "class_conflict_resolution":
                var conflictMode = RequireNonEmptyString(stage, "mode");
                if (conflictMode == "room_wins")
                {
                    using var notRoom = new Mat();
                    Cv2.BitwiseNot(state.Room, notRoom);
                    Cv2.BitwiseAnd(state.Corridor, notRoom, state.Corridor);
                }
                else if (conflictMode == "corridor_wins")
                {
                    using var notCorr = new Mat();
                    Cv2.BitwiseNot(state.Corridor, notCorr);
                    Cv2.BitwiseAnd(state.Room, notCorr, state.Room);
                }
                else if (conflictMode == "nearest_center_wins")
                {
                    ResolveClassConflict(state);
                }
                else
                {
                    throw new InvalidDataException($"不支持的 class_conflict_resolution 模式：{conflictMode}。");
                }
                break;
            case "ignore_route_overlays":
                RequireString(stage, "mode", "HSV_RANGES");
                IgnoreRouteOverlays(state, parameters);
                break;
            case "morph_open":
                MorphEach(state, MorphTypes.Open, ReadSize(stage, "kernel"));
                break;
            case "remove_small_components":
                ReplaceMasks(state,
                    RemoveSmall(state.Room, ReadBoundedInt(stage, "room_min_area", 1, int.MaxValue)),
                    RemoveSmall(state.Corridor, ReadBoundedInt(stage, "corridor_min_area", 1, int.MaxValue)));
                break;
            case "morph_close":
                foreach (var size in ReadSizes(stage, "kernels"))
                    MorphEach(state, MorphTypes.Close, size);
                break;
            case "fill_holes":
                RequireString(stage, "mode", "all_enclosed");
                ReplaceMasks(state, FillAllHoles(state.Room), FillAllHoles(state.Corridor));
                break;
            case "directional_bridge":
                ReplaceMasks(state,
                    DirectionalBridge(state.Room, ReadBoundedInt(stage, "horizontal_gap_px", 1, 255),
                        ReadBoundedInt(stage, "vertical_gap_px", 1, 255)),
                    DirectionalBridge(state.Corridor, ReadBoundedInt(stage, "horizontal_gap_px", 1, 255),
                        ReadBoundedInt(stage, "vertical_gap_px", 1, 255)));
                break;
            case "fill_small_holes":
                ReplaceMasks(state,
                    FillSmallHoles(state.Room, ReadBoundedInt(stage, "room_max_hole_area", 1, int.MaxValue)),
                    FillSmallHoles(state.Corridor, ReadBoundedInt(stage, "corridor_max_hole_area", 1, int.MaxValue)));
                break;
            case "contours":
                state.RoomRetrieval = ReadRetrieval(stage);
                state.CorridorRetrieval = state.RoomRetrieval;
                if (stage.TryGetProperty("approx_poly_dp_epsilon", out var epsVal) && epsVal.TryGetDouble(out var eps) && eps >= 0d)
                    state.ApproxPolyDpEpsilon = eps;
                if (stage.TryGetProperty("min_perimeter_px", out var minPerimVal) && minPerimVal.TryGetDouble(out var minP) && minP >= 0d)
                    state.MinPerimeterPx = minP;
                if (stage.TryGetProperty("orthogonal_snap_px", out var snapVal) && snapVal.TryGetInt32(out var sPx) && sPx >= 0)
                    state.OrthogonalSnapPx = sPx;
                break;
            case "room_contours":
                state.RoomRetrieval = ReadRetrieval(stage);
                if (stage.TryGetProperty("approx_poly_dp_epsilon", out var epsValR) && epsValR.TryGetDouble(out var epsR) && epsR >= 0d)
                    state.ApproxPolyDpEpsilon = epsR;
                if (stage.TryGetProperty("min_perimeter_px", out var minPerimValR) && minPerimValR.TryGetDouble(out var minPR) && minPR >= 0d)
                    state.MinPerimeterPx = minPR;
                if (stage.TryGetProperty("orthogonal_snap_px", out var snapValR) && snapValR.TryGetInt32(out var sPxR) && sPxR >= 0)
                    state.OrthogonalSnapPx = sPxR;
                break;
            case "corridor_contours":
                state.CorridorRetrieval = ReadRetrieval(stage);
                if (stage.TryGetProperty("approx_poly_dp_epsilon", out var epsValC) && epsValC.TryGetDouble(out var epsC) && epsC >= 0d)
                    state.ApproxPolyDpEpsilon = epsC;
                if (stage.TryGetProperty("min_perimeter_px", out var minPerimValC) && minPerimValC.TryGetDouble(out var minPC) && minPC >= 0d)
                    state.MinPerimeterPx = minPC;
                if (stage.TryGetProperty("min_hole_area_px", out var minHoleValC) && minHoleValC.TryGetDouble(out var minHC) && minHC >= 0d)
                    state.CorridorMinHoleAreaPx = minHC;
                if (stage.TryGetProperty("orthogonal_snap_px", out var snapValC) && snapValC.TryGetInt32(out var sPxC) && sPxC >= 0)
                    state.OrthogonalSnapPx = sPxC;
                break;
            case "source_edge_evidence":
                ExecuteSourceEdgeEvidence(state, parameters, stage);
                break;
            case "draw_edges":
                if (ReadBoolean(stage, "antialias"))
                    throw new InvalidDataException("IDVA 1.1 不支持抗锯齿线图。");
                var lineWidth = ReadBoundedInt(stage, "line_width_px", 1, 16);
                var drawMode = stage.TryGetProperty("mode", out var mProp) ? mProp.GetString() : "CONTOURS";
                if (stage.TryGetProperty("approx_poly_dp_epsilon", out var epsValD) && epsValD.TryGetDouble(out var epsD) && epsD >= 0d)
                    state.ApproxPolyDpEpsilon = epsD;
                if (stage.TryGetProperty("min_perimeter_px", out var minPerimValD) && minPerimValD.TryGetDouble(out var minPD) && minPD >= 0d)
                    state.MinPerimeterPx = minPD;
                if (stage.TryGetProperty("orthogonal_snap_px", out var snapValD) && snapValD.TryGetInt32(out var sPxD) && sPxD >= 0)
                    state.OrthogonalSnapPx = sPxD;
                if (drawMode is "CONTOURS" or null)
                {
                    state.CombineEdges(lineWidth);
                }
                else if (drawMode is "SOURCE_EDGE_GATED" or "CANNY_BOUNDARY_GATED")
                {
                    DrawSourceEdgeGated(state, parameters, stage, lineWidth);
                }
                else
                {
                    throw new InvalidDataException($"不支持的 draw_edges 模式：{drawMode}。");
                }
                break;
        }
    }

    private static void ValidateRuntime(JsonElement root)
    {
        var runtime = RequireObject(root, "runtime");
        RequireString(runtime, "engine", "idvb-opencv-pipeline");
        RequireString(runtime, "language", "declarative-json");
        RequireString(runtime, "minimum_engine_version", "1.0");
    }

    private static void ValidateGeometry(JsonElement root)
    {
        var geometry = RequireObject(root, "geometry_policy");
        if (!ReadBoolean(geometry, "preserve_input_size")
            || ReadBoolean(geometry, "allow_resize")
            || ReadBoolean(geometry, "allow_rotation")
            || ReadBoolean(geometry, "allow_warp"))
        {
            throw new InvalidDataException("IDVA 必须保持输入尺寸，且禁止 resize、rotate 和 warp。");
        }
    }

    private static void ValidateInputOutput(JsonElement root)
    {
        var input = RequireObject(root, "input");
        RequireString(input, "type", "raster-image");
        RequireString(input, "color_order", "BGR");
        var output = RequireObject(root, "output");
        RequireString(output, "type", "binary-edge-map");
        if (ReadBoundedInt(output, "background", 0, 255) != 0
            || ReadBoundedInt(output, "edge", 0, 255) != 255)
            throw new InvalidDataException("线图输出必须使用 0 作为背景、255 作为结构边。");
    }
}
