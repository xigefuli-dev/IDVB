using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.PoseGraph;

public sealed class RootPropagationPoseGraphOptimizer : IPoseGraphOptimizer
{
    public Task<IReadOnlyDictionary<Guid, SurveyLayerTransform>> OptimizeAsync(
        SurveyProjectSnapshot project,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, SurveyLayerTransform>();
        foreach (var floor in project.Floors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layers = project.Layers
                .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted)
                .ToDictionary(item => item.LayerId);
            if (layers.Count == 0)
                continue;
            var rootId = floor.RootLayerId is { } configured && layers.ContainsKey(configured)
                ? configured
                : layers.Values.OrderBy(item => item.ZOrder).First().LayerId;
            result[rootId] = layers[rootId].AutomaticTransform;
            var pending = new Queue<Guid>();
            pending.Enqueue(rootId);
            var constraints = project.Constraints
                .Where(item => item.FloorId == floor.FloorId && item.IsAccepted)
                .OrderByDescending(item => item.Confidence)
                .ToArray();
            while (pending.Count > 0)
            {
                var knownId = pending.Dequeue();
                var knownTransform = result[knownId];
                foreach (var edge in constraints.Where(item => item.TargetLayerId == knownId))
                {
                    if (!layers.ContainsKey(edge.SourceLayerId) || result.ContainsKey(edge.SourceLayerId))
                        continue;
                    result[edge.SourceLayerId] = Compose(knownTransform, edge.RelativeTransform);
                    pending.Enqueue(edge.SourceLayerId);
                }
            }
        }
        return Task.FromResult<IReadOnlyDictionary<Guid, SurveyLayerTransform>>(result);
    }

    public static SurveyLayerTransform Compose(
        SurveyLayerTransform parent,
        SurveyLayerTransform child)
    {
        var translation = parent.Transform(new SurveyWorldPoint(
            child.TranslationX,
            child.TranslationY));
        return new SurveyLayerTransform(
            translation.X,
            translation.Y,
            parent.RotationDegrees + child.RotationDegrees,
            parent.ScaleX * child.ScaleX,
            parent.ScaleY * child.ScaleY);
    }
}
