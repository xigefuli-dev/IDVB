using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>ISettingsRepository 适配器 — 委托给 MapRuntimeSettingsRepository。</summary>
public sealed class SettingsRepositoryAdapter : ISettingsRepository
{
    private readonly MapRuntimeSettingsRepository _repo = new();

    public Task<object> LoadAsync() =>
        _repo.LoadAsync().ContinueWith(t => (object)t.Result);

    public Task SaveAsync(object settings) =>
        _repo.SaveAsync((MapRuntimeSettings)settings);
}
