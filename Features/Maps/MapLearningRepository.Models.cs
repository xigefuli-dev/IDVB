using System.Security.Cryptography;
using System.Text;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapLearningRepository
{
    public async Task<bool> VerifyModelAsync(
        string version,
        CancellationToken cancellationToken)
    {
        var manifest = await LoadModelManifestAsync(version, cancellationToken);
        if (manifest is null
            || manifest.ArchitectureVersion
                != MapLearningModelContract.ArchitectureVersion
            || manifest.PreprocessingVersion != MapLearningPreprocessor.Version)
        {
            return false;
        }
        var hash = await ComputeWeightsHashAsync(
            GetModelDirectory(version), cancellationToken);
        return string.Equals(hash, manifest.WeightsSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MapModelManifest> CommitModelAsync(
        SiameseMapNetwork network,
        MapModelManifest draft,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        EnsureCreated();
        var temporaryDirectory = Path.Combine(ModelsDirectory,
            ".candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            network.Save(temporaryDirectory);
            var weightsHash = await ComputeWeightsHashAsync(
                temporaryDirectory, cancellationToken);
            var identityText = string.Join('|', weightsHash,
                draft.DatasetRootHash, draft.ParentVersion,
                draft.ArchitectureVersion, draft.PreprocessingVersion);
            var identityHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
            var version = CreateNextModelVersion(identityHash);
            var committed = draft with
            {
                Version = version,
                WeightsSha256 = weightsHash
            };
            await WriteJsonAsync(Path.Combine(temporaryDirectory,
                "manifest.json"), committed, cancellationToken);
            Directory.Move(temporaryDirectory, GetModelDirectory(version));
            return committed;
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PromoteAsync(
        MapModelManifest manifest,
        CancellationToken cancellationToken)
    {
        await UpdateModelManifestAsync(manifest with
        {
            State = MapModelVersionState.Stable
        }, cancellationToken);
        await WriteReferenceAsync(CurrentReferencePath, manifest.Version,
            cancellationToken);
        await WriteReferenceAsync(BestExperimentalReferencePath,
            manifest.Version, cancellationToken);
        await WriteReferenceAsync(LastKnownGoodReferencePath,
            manifest.Version, cancellationToken);
    }

    public async Task ActivateExperimentalAsync(
        string version,
        CancellationToken cancellationToken)
    {
        if (!await VerifyModelAsync(version, cancellationToken))
            throw new InvalidDataException(
                "目标实验模型不存在、损坏或与当前架构不兼容。");
        await WriteReferenceAsync(BestExperimentalReferencePath, version,
            cancellationToken);
    }

    public async Task RestoreAsync(
        string version,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!await VerifyModelAsync(version, cancellationToken))
            throw new InvalidDataException(
                "目标模型不存在、损坏或与当前架构不兼容。");
        await WriteReferenceAsync(CurrentReferencePath, version,
            cancellationToken);
        var state = await LoadStateAsync(cancellationToken);
        await SaveStateAsync(state with { LastRollbackReason = reason },
            cancellationToken);
    }

    private static async Task<string> ComputeWeightsHashAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in SiameseMapNetwork.WeightFileNames)
        {
            var bytes = await File.ReadAllBytesAsync(
                Path.Combine(directory, name), cancellationToken);
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
