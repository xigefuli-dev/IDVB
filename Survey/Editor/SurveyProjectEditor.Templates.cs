using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor
{
    private void BeginTemplateEdit()
    {
        if (_templatePicker?.SelectedItem is not SurveyColorTemplate selected)
        {
            SetStatus("请先选择要编辑的模板。", isError: true);
            return;
        }

        _editingTemplateId = selected.Id;
        _draftTemplateEntries.Clear();
        _draftTemplateEntries.AddRange(selected.Entries);
        _templateMode = SurveyTemplateMode.Create;
        _canvas.DisarmTemplateColorSampler();
        if (_templateModePicker is not null)
            _templateModePicker.SelectedIndex = 0;
        SetStatus($"正在编辑模板“{selected.Name}”。", false);
    }

    private void CancelTemplateEdit()
    {
        _editingTemplateId = null;
        _draftTemplateEntries.Clear();
        _templateMode = SurveyTemplateMode.Apply;
        _canvas.DisarmTemplateColorSampler();
        if (_templateModePicker is not null)
            _templateModePicker.SelectedIndex = 1;
        SetStatus("已取消模板编辑。", false);
    }

    private async Task SaveCurrentTemplateAsync()
    {
        if (_templateSaveInProgress)
            return;
        if (_draftTemplateEntries.Count == 0)
        {
            SetStatus("模板至少需要一种颜色。", isError: true);
            return;
        }
        _templateSaveInProgress = true;
        if (_templateSaveButton is not null)
            _templateSaveButton.IsEnabled = false;
        var name = _templateNameBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = $"模板 {_templates.Count + 1}";
        var wasEditing = _editingTemplateId is not null;
        var template = new SurveyColorTemplate(
            _editingTemplateId ?? Guid.NewGuid(),
            name,
            _draftTemplateEntries.ToArray());
        var next = _editingTemplateId is { } editingId
            ? _templates
                .Select(item => item.Id == editingId ? template : item)
                .ToArray()
            : _templates.Append(template).ToArray();
        try
        {
            await _templateStore.SaveAsync(next, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            SetStatus($"模板保存失败：{exception.Message}", isError: true);
            return;
        }
        finally
        {
            _templateSaveInProgress = false;
            if (_templateSaveButton is not null)
                _templateSaveButton.IsEnabled = _draftTemplateEntries.Count > 0;
        }

        _templates.Clear();
        _templates.AddRange(next);
        _editingTemplateId = null;
        _draftTemplateEntries.Clear();
        RefreshTemplateDraftList();
        if (_templateSaveButton is not null)
            _templateSaveButton.Content = "保存模板";
        if (_templateCancelEditButton is not null)
            _templateCancelEditButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (_templateNameBox is not null)
            _templateNameBox.Text = $"模板 {_templates.Count + 1}";
        SetStatus(
            wasEditing
                ? $"模板“{template.Name}”已更新，共 {template.Entries.Count} 种颜色。"
                : $"模板“{template.Name}”已保存，共 {template.Entries.Count} 种颜色。",
            false);
    }
}
