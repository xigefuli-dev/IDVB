namespace IDVBuff.Survey.Domain;

/// <summary>
/// Semantic role used by the template quantizer when it evaluates local image structure.
/// </summary>
public enum SurveyTemplateColorType
{
    Fill,
    Outline,
    Icon
}

/// <summary>
/// One RGB sample and its semantic role in a color template.
/// </summary>
public sealed record SurveyColorTemplateEntry(
    byte R,
    byte G,
    byte B,
    SurveyTemplateColorType Type);

/// <summary>
/// A named color template that can be reused by the survey editor.
/// </summary>
public sealed record SurveyColorTemplate(
    Guid Id,
    string Name,
    IReadOnlyList<SurveyColorTemplateEntry> Entries);
