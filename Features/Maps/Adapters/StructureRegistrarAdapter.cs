using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IStructureRegistrar 适配器 — 委托给 MapStructureRegistrar。</summary>
public sealed class StructureRegistrarAdapter : IStructureRegistrar
{
    private readonly MapStructureRegistrar _registrar;

    public StructureRegistrarAdapter(MapStructurePreprocessor preprocessor)
    {
        _registrar = new MapStructureRegistrar(preprocessor);
    }

    public object Register(object request) =>
        _registrar.Register((MapStructureRegistrationRequest)request);
}
