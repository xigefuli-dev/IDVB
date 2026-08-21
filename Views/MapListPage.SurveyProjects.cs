using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private FrameworkElement CreateSurveyProjectsSection()
    {
        var body = new StackPanel { Spacing = 12 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "测绘项目",
            FontSize = 20,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var hint = new TextBlock
        {
            Text = "在地图候选窗口选择“没有我想要的地图”即可开始",
            FontSize = 12,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(hint, 1);
        header.Children.Add(hint);
        body.Children.Add(header);

        if (_surveyProjects.Count == 0)
        {
            body.Children.Add(new Border
            {
                MinHeight = 92,
                Background = FluentTheme.CardBrush(),
                CornerRadius = new CornerRadius(10),
                Child = new TextBlock
                {
                    Text = "暂无测绘项目。开始快捷扫描后，可从候选窗口进入测绘模式。",
                    FontSize = 14,
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                }
            });
        }
        else
        {
            var cards = new StackPanel { Spacing = 8 };
            foreach (var project in _surveyProjects)
                cards.Children.Add(CreateSurveyProjectCard(project));
            body.Children.Add(cards);
        }

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 18),
            Padding = new Thickness(18),
            Background = FluentTheme.Brush("LayerFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(14),
            Child = body
        };
    }

    private FrameworkElement CreateSurveyProjectCard(SurveyProjectSummary project)
    {
        var root = new Grid
        {
            Padding = new Thickness(14, 11, 12, 11),
            ColumnSpacing = 16,
            Background = FluentTheme.CardBrush()
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(new TextBlock
        {
            Text = project.Name,
            FontSize = 15,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{ProjectStateText(project.State)} · {project.MapClass} · "
                + $"{project.ActiveLayerCount} 个图层 · {project.UnregisteredCount} 个未对齐 · "
                + project.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            FontSize = 12,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        root.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        var edit = CreateSurveyCardButton("打开编辑器");
        edit.Click += async (_, _) => await ShowSurveyProjectEditorAsync(project.ProjectId);
        actions.Children.Add(edit);

        var rename = CreateSurveyCardButton("重命名");
        rename.Click += async (_, _) => await RenameSurveyProjectAsync(project);
        actions.Children.Add(rename);

        var delete = CreateSurveyCardButton("删除");
        delete.Click += async (_, _) => await DeleteSurveyProjectAsync(project);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 1);
        root.Children.Add(actions);

        return new Border
        {
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = root
        };
    }

    private static Button CreateSurveyCardButton(string text) => new()
    {
        Content = text,
        MinWidth = 82,
        MinHeight = 34,
        Padding = new Thickness(12, 5, 12, 5)
    };

    private async Task RenameSurveyProjectAsync(SurveyProjectSummary project)
    {
        var name = new TextBox
        {
            Header = "项目名称",
            Text = project.Name,
            MinWidth = 320
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名测绘项目",
            Content = name,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (string.IsNullOrWhiteSpace(name.Text))
        {
            await ShowMessageAsync("无法重命名", "项目名称不能为空。");
            return;
        }
        var result = await App.Session.RenameSurveyProjectAsync(
            new SurveyProjectRenameRequest(
                Guid.NewGuid(), project.ProjectId, project.Revision, name.Text));
        if (!result.Succeeded)
        {
            await ShowMessageAsync("重命名失败", result.Message ?? "测绘项目名称没有更新。");
            return;
        }
        await ShowListAsync();
    }

    private async Task DeleteSurveyProjectAsync(SurveyProjectSummary project)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除测绘项目？",
            Content = $"“{project.Name}”及其全部图层和资源将被永久删除，此操作无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var result = await App.Session.DeleteSurveyProjectAsync(
            new SurveyProjectDeleteRequest(
                Guid.NewGuid(), project.ProjectId, project.Revision));
        if (!result.Succeeded)
        {
            await ShowMessageAsync("删除失败", result.Message ?? "无法删除测绘项目。");
            return;
        }
        await ShowListAsync();
    }

    private static string ProjectStateText(SurveyProjectState state) => state switch
    {
        SurveyProjectState.Draft => "草稿",
        SurveyProjectState.Collecting => "测绘中",
        SurveyProjectState.NeedsReview => "待检查",
        SurveyProjectState.ReadyToPublish => "可发布",
        SurveyProjectState.Published => "已发布",
        SurveyProjectState.Archived => "已归档",
        _ => state.ToString()
    };
}
