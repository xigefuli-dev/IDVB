using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Editor.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private SurveyProjectEditor? _surveyProjectEditor;

    private Task ShowSurveyProjectEditorAsync(Guid projectId)
    {
        ResetMarkerEditorSession();
        ResetBatchOperation();
        EnterModernEditorEnvironment();
        var coordinator = App.Services.GetRequiredService<ISurveyCoordinator>();
        var editor = new SurveyProjectEditor(coordinator, projectId);
        editor.CloseRequested += async (_, _) => await ShowListAsync();
        _surveyProjectEditor = editor;
        _workflowHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _workflowHost.VerticalAlignment = VerticalAlignment.Stretch;
        _workflowHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _workflowHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        if (ParentScrollViewer is not null)
        {
            ParentScrollViewer.SizeChanged -= SurveyEditorParent_SizeChanged;
            ParentScrollViewer.SizeChanged += SurveyEditorParent_SizeChanged;
            ApplySurveyEditorViewportSize(
                ParentScrollViewer.ActualWidth,
                ParentScrollViewer.ActualHeight);
        }
        else
        {
            ApplySurveyEditorViewportSize(ActualWidth, ActualHeight);
        }
        _workflowHost.Content = editor;
        PlayWorkflowEnterAnimation();
        return Task.CompletedTask;
    }

    private void SurveyEditorParent_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplySurveyEditorViewportSize(e.NewSize.Width, e.NewSize.Height);

    private void ApplySurveyEditorViewportSize(double width, double height)
    {
        if (_surveyProjectEditor is null)
            return;
        _surveyProjectEditor.Width = Math.Max(1d, width);
        _surveyProjectEditor.Height = Math.Max(1d, height);
    }

    private void ResetSurveyProjectEditorSession()
    {
        if (ParentScrollViewer is not null)
            ParentScrollViewer.SizeChanged -= SurveyEditorParent_SizeChanged;
        var editor = _surveyProjectEditor;
        _surveyProjectEditor = null;
        if (editor is null)
            return;
        editor.Dispose();
        if (ReferenceEquals(_workflowHost.Content, editor))
            _workflowHost.Content = null;
    }
}
