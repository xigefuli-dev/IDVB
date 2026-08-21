using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Contracts;

/// <summary>
/// Persistent storage for reusable survey-editor color templates.
/// </summary>
public interface ISurveyTemplateStore
{
    Task<IReadOnlyList<SurveyColorTemplate>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<SurveyColorTemplate> templates,
        CancellationToken cancellationToken = default);
}
