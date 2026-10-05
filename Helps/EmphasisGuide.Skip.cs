namespace IDVBuff.Helps;

public sealed partial class EmphasisGuide
{
    private async Task<bool> ConfirmSkipAfterFailedChecksAsync(
        EmphasisGuideStep step,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _host.XamlRoot,
            Title = "跳过此引导步骤？",
            Content = $"“{step.Title}”连续 5 次检查未通过。\n\n{failureMessage}\n\n你可以继续操作后再检查，或跳过此步骤。",
            PrimaryButtonText = "跳过此步骤",
            CloseButtonText = "继续检查",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowThemedAsync(cancellationToken: cancellationToken) == ContentDialogResult.Primary;
    }
}
