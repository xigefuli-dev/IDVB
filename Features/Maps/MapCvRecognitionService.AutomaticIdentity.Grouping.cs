namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private sealed record AutomaticIdentityGroupSelection(
        AutomaticIdentityPoseEvidence Pose,
        MapVariantGroup? ConfiguredGroup);

    // The caller supplies the repository's complete catalog snapshot and keeps
    // its existing revision, cancellation and budget checks around acceptance.
    // Tags and sequence numbers never create group membership here.
    private static AutomaticIdentityGroupSelection? SelectConfiguredAutomaticIdentityPose(
        Vpsg3LiveObservation observation,
        IReadOnlyList<AutomaticIdentityPoseEvidence> surviving,
        IReadOnlyCollection<AutomaticIdentityFloorWork> unresolved,
        MapCatalogSnapshot catalog)
    {
        if (surviving.Count == 0) return null;
        var first = surviving[0].Owner;
        if (surviving.Any(pose => !string.Equals(pose.Owner.FloorKey, first.FloorKey,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pose.Owner.Map.Class, first.Map.Class,
                StringComparison.OrdinalIgnoreCase))) return null;

        var groups = catalog.VariantGroups.Where(group => group.MapIds.Contains(first.Map.Id))
            .ToArray();
        if (groups.Length == 0)
        {
            // A map outside every configured group retains the original
            // singleton policy, including every unresolved floor contender.
            if (unresolved.Count != 0
                || surviving.Any(pose => !SameAutomaticIdentity(first, pose.Owner))) return null;
            var singleton = SelectAutomaticPose(observation, surviving);
            return singleton is null ? null : new(singleton, null);
        }
        if (groups.Length != 1) return null;

        var configured = groups[0];
        var members = configured.MapIds.ToHashSet();
        if (configured.Id == Guid.Empty || members.Count < 2
            || !string.Equals(configured.Class, first.Map.Class,
                StringComparison.OrdinalIgnoreCase)
            || surviving.Any(pose => !members.Contains(pose.Owner.Map.Id))
            || unresolved.Any(item => !members.Contains(item.Map.Id)
                || !string.Equals(item.FloorKey, first.FloorKey,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.Map.Class, configured.Class,
                    StringComparison.OrdinalIgnoreCase))) return null;

        // Repository validation normally guarantees these members. Retain the
        // same class boundary when consuming an explicitly supplied snapshot.
        var memberMaps = catalog.Maps.Where(map => members.Contains(map.Id)).ToArray();
        if (memberMaps.Length != members.Count || memberMaps.Any(map => !string.Equals(
                map.Class, configured.Class, StringComparison.OrdinalIgnoreCase))) return null;

        foreach (var member in memberMaps.OrderBy(map => map.SequenceNumber).ThenBy(map => map.Id))
        {
            var ownPoses = surviving.Where(pose => pose.Owner.Map.Id == member.Id).ToArray();
            if (ownPoses.Length == 0) continue;
            // Reference origins and dimensions differ between members. Pose
            // agreement and aperture quality are compared within one member
            // only; CanAccept and the existing strict evidence stay unchanged.
            var selected = SelectAutomaticPose(observation, ownPoses);
            if (selected is null) continue;
            var confirmed = configured.Clone();
            confirmed.MapIds = memberMaps.OrderBy(map => map.SequenceNumber)
                .ThenBy(map => map.Id).Select(map => map.Id).ToList();
            return new(selected, confirmed);
        }
        return null;
    }
}
