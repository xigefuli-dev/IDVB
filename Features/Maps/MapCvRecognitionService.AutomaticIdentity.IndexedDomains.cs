using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private AutomaticIdentityFloorWork[]? TryClassifyAutomaticEntryDomains(
        Vpsg3LiveObservation observation, AutomaticGeometryInput input,
        AutomaticIdentityFloorWork[] work, MapCatalogRevision revision,
        MapCatalogSnapshot catalog, CancellationToken cancellationToken)
    {
        if (input.Corners.Count < 6
            || input.GateDetection.BudgetExceeded
            || input.GateDetection.StopReason != GateSearchStopReason.Completed
            || input.GateDetection.SearchModeUsed != GateSearchMode.FullSearch
            || catalog.Revision != revision) return null;
        var timer = Stopwatch.StartNew();
        var witnesses = BuildAutomaticEntryWallWitnesses(observation, input, cancellationToken);
        if (witnesses.Count < 6) return null;
        var witnessPreparationMs = timer.Elapsed.TotalMilliseconds;
        var cfg = Vpsg3TuningConfig.Default;
        var unanchored = input.Gates.Count == 0;
        var searches = new Dictionary<AutomaticIdentityFloorWork, MapFrontEntryDomainSearchResult>();
        var sources = new Dictionary<AutomaticIdentityFloorWork, MapFrontEntryReferenceIndex>();
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = item.Lease?.Floor.AutomaticEntryIndex;
            string[] requiredRoles = item.FloorKey.ToLowerInvariant() switch
            {
                "1f" => ["main-entrance", "side-entrance"],
                "2f" => ["second-floor-primary"],
                _ => []
            };
            if (source is null || source.CacheKey != item.Lease!.Floor.CacheKey
                || source.CatalogRevision != revision || !source.CoverageIssues.IsEmpty
                || requiredRoles.Length == 0 || !unanchored && requiredRoles.Any(role =>
                    !source.RoleCoverage.Any(coverage => coverage.RoleKey == role
                        && coverage.UsableAnchorCount > 0))) return null;
            Func<bool> budgetExpired = () => MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0;
            var result = unanchored
                ? MapFrontEntryDomainSearch.SolveUnanchored(source, witnesses,
                    cfg.MinSupportedScale, cfg.MaxSupportedScale, cancellationToken, budgetExpired)
                : MapFrontEntryDomainSearch.Solve(source, witnesses, input.Gates,
                    cfg.MinSupportedScale, cfg.MaxSupportedScale, cancellationToken, budgetExpired);
            item.IndexedDomainAttempted = true;
            item.IndexedDomainComparisonComplete = result.Complete;
            item.IndexedDomainsVisited = result.VisitedDomains;
            item.IndexedDomainsRemaining = result.RemainingDomains.Count;
            searches[item] = result; sources[item] = source;
            if (!result.Complete) return null;
        }
        var possible = work.Where(item => searches[item].RemainingDomains.Count > 0).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0
            || revision != _catalogRevision || revision != _repository.GetCatalogRevision()) return null;
        // With no observed entrance there is no independent anchor constraint.
        // A fully contradictory extraction can contain decorative boundaries;
        // keep the established native/strict path without committing exclusions.
        if (unanchored && possible.Length == 0)
        {
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "无入口墙段未保留地图，保留原识别流程", details: new()
                {
                    ["domainMode"] = "full-floor-wall",
                    ["wallWitnessCount"] = witnesses.Count,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["comparisonComplete"] = true,
                    ["identityExclusionsCommitted"] = false
                });
            return null;
        }
        foreach (var item in work)
        {
            if (searches[item].RemainingDomains.Count == 0)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.DominatedByObservedStructure;
                item.FailureReason = "全部支持尺度与位置域均与当前实测墙段矛盾。";
            }
            else
            {
                // Identity pruning does not authorize a pose. Existing native,
                // geometry and strict validation still own the surviving floor.
                item.IndexedDomainSource = sources[item];
                item.IndexedWallWitnesses = witnesses;
            }
        }
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "自动入口缓存结构筛选", details: new()
            {
                ["domainMode"] = unanchored ? "full-floor-wall" : "observed-entrance",
                ["observedGateCount"] = input.Gates.Count,
                ["wallWitnessCount"] = witnesses.Count,
                ["wallWitnessPreparationMs"] = witnessPreparationMs,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["possibleMapIds"] = possible.Select(item => item.Map.Id).ToArray(),
                ["candidates"] = work.Select(item => new { item.Map.Id, item.Map.SequenceNumber,
                    item.FloorKey, item.IndexedDomainComparisonComplete,
                    item.IndexedDomainsVisited, item.IndexedDomainsRemaining }).ToArray()
            });
        return possible;
    }
}
