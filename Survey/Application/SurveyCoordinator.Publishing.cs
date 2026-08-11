using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator
{
    public async Task<SurveyOperationResult<SurveyDualOutput>> RenderOutputsAsync(
        Guid projectId,
        string floorKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (_visualComposer is null || _structureFusion is null)
        {
            return SurveyOperationResult<SurveyDualOutput>.Failure(
                SurveyErrorCode.InvalidState,
                "当前运行环境没有注册测绘融合模块。");
        }
        var snapshot = await _projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return SurveyOperationResult<SurveyDualOutput>.Failure(
                SurveyErrorCode.ProjectNotFound,
                "测绘项目不存在。");
        }
        if (!snapshot.ActiveLayers(floorKey).Any())
        {
            return SurveyOperationResult<SurveyDualOutput>.Failure(
                SurveyErrorCode.InvalidState,
                "当前楼层没有可发布的测绘图层。");
        }
        try
        {
            var visual = await _visualComposer.ComposeAsync(snapshot, floorKey, cancellationToken)
                .ConfigureAwait(false);
            var structure = await _structureFusion.FuseAsync(snapshot, floorKey, cancellationToken)
                .ConfigureAwait(false);
            return SurveyOperationResult<SurveyDualOutput>.Success(new SurveyDualOutput(
                projectId,
                snapshot.Project.Revision,
                floorKey,
                visual,
                structure));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SurveyOperationResult<SurveyDualOutput>.Failure(
                SurveyErrorCode.Cancelled,
                "测绘输出生成已取消。");
        }
        catch (Exception exception)
        {
            return SurveyOperationResult<SurveyDualOutput>.Failure(
                SurveyErrorCode.Unknown,
                exception.Message,
                Guid.NewGuid().ToString("N"));
        }
    }
}
