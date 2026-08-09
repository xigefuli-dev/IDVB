// IDVB Remaster Phase 1.2 — Resolution Profile Manager with Hot-Swap

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Infrastructure.Configuration;

/// <summary>
/// 分辨率配置热切换核心。管理预设列表、匹配、切换，
/// 切换时广播 ResolutionChanged 通知所有下游服务。
/// </summary>
public sealed class ResolutionProfileManager : IResolutionProfileService, IDisposable
{
    private readonly TomlConfigProvider _config;
    private readonly List<ResolutionTuningProfile> _profiles;
    private readonly object _lock = new();

    public event EventHandler? ResolutionChanged;

    public ResolutionProfileManager(TomlConfigProvider config)
    {
        _config = config;
        _profiles = LoadBuiltInProfiles();
    }

    /// <inheritdoc />
    public IReadOnlyList<ResolutionTuningProfile> GetAvailableProfiles()
    {
        lock (_lock) return _profiles.ToList();
    }

    /// <inheritdoc />
    public async Task SetActiveProfileAsync(string profileName)
    {
        lock (_lock)
        {
            var profile = _profiles.FirstOrDefault(p =>
                string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
                throw new ArgumentException($"Resolution profile '{profileName}' not found.", nameof(profileName));
        }

        _config.SetActivePreset(profileName);
        await Task.CompletedTask; // 同步操作，但保留异步签名供将来扩展

        OnResolutionChanged();
    }

    /// <summary>
    /// 根据窗口尺寸和 DPI 自动匹配最合适的分辨率预设。
    /// </summary>
    public string? MatchProfile(int clientWidth, int clientHeight, int dpi)
    {
        lock (_lock)
        {
            // 1. 精确匹配
            var exact = _profiles
                .Where(p => p.ClientWidth == clientWidth && p.ClientHeight == clientHeight)
                .OrderByDescending(p => p.Dpi == dpi)
                .FirstOrDefault();
            if (exact != null) return exact.Name;

            // 2. 物理尺寸模糊匹配（宽高差 ≤100px，DPI 仅次级排序）
            const int tolerance = 100;
            var fuzzy = _profiles
                .OrderBy(p => Math.Abs(p.ClientWidth - clientWidth) + Math.Abs(p.ClientHeight - clientHeight))
                .ThenByDescending(p => p.Dpi == dpi)
                .FirstOrDefault(p =>
                    Math.Abs(p.ClientWidth - clientWidth) <= tolerance &&
                    Math.Abs(p.ClientHeight - clientHeight) <= tolerance);
            if (fuzzy != null) return fuzzy.Name;

            // 3. 同 DPI 宽高比匹配
            if (clientHeight > 0)
            {
                var targetRatio = (double)clientWidth / clientHeight;
                var ratioMatch = _profiles
                    .Where(p => p.ClientHeight > 0)
                    .OrderBy(p => Math.Abs((double)p.ClientWidth / p.ClientHeight - targetRatio))
                    .ThenByDescending(p => p.Dpi == dpi)
                    .FirstOrDefault(p =>
                        Math.Abs((double)p.ClientWidth / p.ClientHeight - targetRatio) < 0.05);
                if (ratioMatch != null) return ratioMatch.Name;
            }

            return null; // 无匹配
        }
    }

    /// <summary>
    /// 自动匹配并切换分辨率预设。
    /// </summary>
    public async Task<bool> TryAutoMatchAsync(int clientWidth, int clientHeight, int dpi)
    {
        var match = MatchProfile(clientWidth, clientHeight, dpi);
        if (match == null) return false;
        await SetActiveProfileAsync(match);
        return true;
    }

    private void OnResolutionChanged()
    {
        ResolutionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 内置出厂预设。用户可通过添加 TOML 文件扩展。
    /// </summary>
    private static List<ResolutionTuningProfile> LoadBuiltInProfiles()
    {
        return
        [
            new ResolutionTuningProfile
            {
                Name = "1920x1080 @ 120 DPI",
                ClientWidth = 1920,
                ClientHeight = 1080,
                Dpi = 120,
                MaximumChamferPixels = 4.5,
                MinimumEdgeCoverage = 0.30,
                EdgeDistanceTolerancePixels = 3.5,
                FastCoarseMaxDimension = 180,
                FastCoarseDownsampleFactor = 2,
                ScaleSearchRadius = 0.04,
                MinimumCandidateMargin = 0.03,
            },
            new ResolutionTuningProfile
            {
                Name = "2560x1440 @ 120 DPI",
                ClientWidth = 2560,
                ClientHeight = 1440,
                Dpi = 120,
                MaximumChamferPixels = 3.5,
                MinimumEdgeCoverage = 0.40,
                MinimumCandidateMargin = 0.04,
                FastCoarseMaxDimension = 160,
            },
            new ResolutionTuningProfile
            {
                Name = "2560x1600 @ 120 DPI",
                ClientWidth = 2560,
                ClientHeight = 1600,
                Dpi = 120,
                MinimumEdgeCoverage = 0.55,
                MinimumCandidateMargin = 0.08,
                VectorErrorTolerance = 0.04,
            },
        ];
    }

    public void Dispose() { /* no-op */ }
}
