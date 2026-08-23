using IDVBuff.Pipeline;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    private readonly MapStructurePreprocessor _preprocessor;
    private readonly object _registrationGate = new();

    /// <summary>
    /// When baseline scale is below one, downsample the reference instead of
    /// enlarging the query so its edge geometry stays sharp.
    /// </summary>
    private ReciprocalScaleContext _currentReciprocalScale =
        ReciprocalScaleContext.None;

    internal sealed class ReciprocalScaleContext
    {
        public double ReferenceScale { get; init; } = 1d;
        public Mat? StructureMask { get; init; }
        public static readonly ReciprocalScaleContext None = new();
    }

    public MapStructureRegistrar(MapStructurePreprocessor preprocessor)
    {
        _preprocessor = preprocessor;
    }

    public MapStructureRegistrationResult Register(
        MapStructureRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var registration = MapOperationTraceAmbient.StartChild(
            "structure_registration",
            MapOperationWaitKind.Compute);
        lock (_registrationGate)
        {
            var tuning = request.Tuning.Clone();
            tuning.Channel = request.Channel;
            tuning.Normalize();

            var savedReciprocalScale = _currentReciprocalScale;
            _currentReciprocalScale = ReciprocalScaleContext.None;
            try
            {
                return RegisterInternal(request, tuning);
            }
            finally
            {
                _currentReciprocalScale = savedReciprocalScale;
            }
        }
    }
}
