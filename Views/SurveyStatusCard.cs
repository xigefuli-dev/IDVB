using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

internal sealed class SurveyStatusCard : UserControl
{
    private readonly TextBlock _title = new()
    {
        FontSize = 17,
        Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
    };
    private readonly TextBlock _state = new()
    {
        FontSize = 13,
        Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
    };
    private readonly TextBlock _counts = new()
    {
        FontSize = 13,
        Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
    };
    private readonly TextBlock _detail = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button _pause = new() { MinWidth = 90 };
    private bool _isPaused;

    public SurveyStatusCard()
    {
        Margin = new Thickness(0, 8, 0, 4);
        var content = new Grid { RowSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(_title);
        Grid.SetRow(_state, 1);
        content.Children.Add(_state);
        Grid.SetRow(_counts, 2);
        content.Children.Add(_counts);
        Grid.SetRow(_detail, 3);
        content.Children.Add(_detail);
        _pause.Click += (_, _) => PauseRequested?.Invoke(this, !_isPaused);
        Grid.SetColumn(_pause, 1);
        Grid.SetRowSpan(_pause, 2);
        content.Children.Add(_pause);
        Content = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            Background = FluentTheme.CardBrush(),
            Child = content
        };
        Update(SurveyStatusSnapshot.Inactive);
    }

    public event EventHandler<bool>? PauseRequested;

    public void Update(SurveyStatusSnapshot status)
    {
        _title.Text = status.ProjectId is null
            ? "测绘模式 · 未激活"
            : $"测绘模式 · {status.ProjectName}";
        _state.Text = status.ProjectId is null
            ? "从地图候选窗口选择“没有我想要的地图”后开始记录。"
            : $"运行：{RuntimeText(status.RuntimeState)} · 项目：{ProjectText(status.ProjectState)}"
                + (string.IsNullOrWhiteSpace(status.FloorKey) ? string.Empty : $" · 楼层 {status.FloorKey.ToUpperInvariant()}");
        _counts.Text = status.ProjectId is null
            ? "没有活动测绘项目"
            : $"观测 {status.ObservationCount} · 已对齐 {status.RegisteredCount} · "
                + $"未对齐 {status.UnregisteredCount} · 已删除 {status.DeletedCount} · 修订 {status.Revision}";
        _detail.Text = status.IsSaving
            ? "正在自动保存……"
            : status.LastErrorCode != SurveyErrorCode.None
                ? $"{status.LastErrorCode}：{status.LastMessage} · 诊断 {status.DiagnosticId}"
                : status.LastMessage ?? "状态已同步。";
        _detail.Foreground = status.LastErrorCode == SurveyErrorCode.None
            ? FluentTheme.Brush("TextFillColorSecondaryBrush")
            : FluentTheme.Brush("SystemFillColorCriticalBrush");
        _isPaused = status.RuntimeState == SurveyRuntimeState.Paused;
        _pause.Content = _isPaused ? "继续测绘" : "暂停测绘";
        _pause.IsEnabled = status.ProjectId is not null
            && status.RuntimeState is not SurveyRuntimeState.Inactive
            and not SurveyRuntimeState.Ending
            and not SurveyRuntimeState.Faulted;
    }

    private static string RuntimeText(SurveyRuntimeState state) => state switch
    {
        SurveyRuntimeState.Inactive => "未激活",
        SurveyRuntimeState.Activating => "正在启动",
        SurveyRuntimeState.WaitingForMapOpen => "等待地图打开",
        SurveyRuntimeState.WaitingForStableFrame => "等待稳定画面",
        SurveyRuntimeState.ProcessingObservation => "处理观测",
        SurveyRuntimeState.Committing => "正在保存",
        SurveyRuntimeState.WaitingForNextOpen => "等待下次打开",
        SurveyRuntimeState.Paused => "已暂停",
        SurveyRuntimeState.Ending => "正在结束",
        SurveyRuntimeState.Faulted => "故障",
        _ => state.ToString()
    };

    private static string ProjectText(SurveyProjectState? state) => state switch
    {
        SurveyProjectState.Draft => "草稿",
        SurveyProjectState.Collecting => "测绘中",
        SurveyProjectState.NeedsReview => "待检查",
        SurveyProjectState.ReadyToPublish => "可发布",
        SurveyProjectState.Published => "已发布",
        SurveyProjectState.Archived => "已归档",
        _ => "未创建"
    };
}
