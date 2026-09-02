using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private readonly ComboBox _candidateDecisionMode = new()
    {
        Header = "地图选择引擎",
        MinWidth = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
        ItemsSource = new[]
        {
            new MapDecisionModeChoice(MapCandidateDecisionMode.Traditional, "传统算法"),
            new MapDecisionModeChoice(MapCandidateDecisionMode.Fusion, "融合"),
            new MapDecisionModeChoice(MapCandidateDecisionMode.ModelOnly, "仅模型")
        },
        DisplayMemberPath = nameof(MapDecisionModeChoice.DisplayName)
    };
    private readonly ToggleSwitch _continuousLearningToggle = new()
    {
        Header = "提供模型改进样本",
        OffContent = "关闭（不会保存或传输训练样本）",
        OnContent = "开启（保存并按隐私设置传输脱敏样本）"
    };
    private readonly ToggleSwitch _automaticModelTrainingToggle = new()
    {
        Header = "对局结束后自动训练",
        OffContent = "关闭（只保存样本，由我手动训练）",
        OnContent = "开启（有新人工标签时自动后台训练）"
    };
    private readonly TextBlock _mapLearningState = CreateMutedText();
    private readonly TextBlock _mapLearningProgressText = CreateMutedText();
    private readonly TextBlock _gpuSidecarState = CreateMutedText();
    private readonly ProgressBar _mapLearningProgress = new()
    {
        Minimum = 0,
        Maximum = 1,
        Visibility = Visibility.Collapsed
    };
    private readonly DispatcherTimer _mapLearningProgressTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private readonly Button _trainMapModelButton = new() { Content = "立即训练" };
    private readonly Button _initializeGpuButton = new() { Content = "初始化 GPU" };
    private readonly Button _exportMapTrainingButton = new() { Content = "导出脱敏训练包" };
    private readonly Button _clearMapTrainingButton = new() { Content = "清理训练样本" };
    private readonly Button _mapModelHistoryButton = new() { Content = "模型历史与恢复" };

    private void AttachMapLearningPanel()
    {
        if (!IDVBuff.Lifecycle.MainProgramPreferences.Load().DeveloperMode)
            return;
        if (_root is null
            || _root.Children.Count < 2
            || _root.Children[1] is not StackPanel content)
        {
            return;
        }
        var backgroundIndex = content.Children.IndexOf(_backgroundScanToggle);
        content.Children.Insert(
            backgroundIndex >= 0 ? backgroundIndex + 1 : 0,
            BuildMapLearningPanel());
        _mapLearningProgressTimer.Tick += (_, _) =>
            UpdateMapLearningProgress(_runtime.MapLearningStatus);
        _mapLearningProgressTimer.Start();
    }

    private UIElement BuildMapLearningPanel()
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(_exportMapTrainingButton);
        actions.Children.Add(_clearMapTrainingButton);

        _continuousLearningToggle.Toggled += ContinuousLearning_Toggled;
        _exportMapTrainingButton.Click += ExportMapTraining_Click;
        _clearMapTrainingButton.Click += ClearMapTraining_Click;

        return new Expander
        {
            Header = "模型改进样本",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "主程序不包含模型训练或推理运行时。"
                            + "只有人工选择或纠错会保存为脱敏样本；"
                            + "模型训练由用户自行准备的外部组件完成。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    _continuousLearningToggle,
                    _mapLearningState,
                    actions
                }
            }
        };
    }

    private async void AutomaticModelTraining_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        await RunMapLearningActionAsync(() =>
            _runtime.SetAutomaticMapModelTrainingEnabledAsync(
                _automaticModelTrainingToggle.IsOn));
    }

    private async void CandidateDecisionMode_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_refreshing
            || _candidateDecisionMode.SelectedItem
                is not MapDecisionModeChoice choice)
        {
            return;
        }
        await RunMapLearningActionAsync(() =>
            _runtime.SetCandidateDecisionModeAsync(choice.Mode));
    }

    private async void ContinuousLearning_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        await RunMapLearningActionAsync(() =>
            _runtime.SetMapImprovementDataCollectionEnabledAsync(
                _continuousLearningToggle.IsOn));
    }

    private async void TrainMapModel_Click(object sender, RoutedEventArgs e)
    {
        await RunMapLearningActionAsync(async () =>
        {
            _status.Text = "正在后台训练地图模型；可以继续操作主窗口。";
            var result = await _runtime.TrainMapModelNowAsync();
            _status.Text = result.Trained
                ? $"模型 {result.Version} 训练完成；"
                    + (result.Promoted ? "已晋级。" : $"未晋级：{result.Reason}")
                : result.Reason;
        });
    }

    private async void InitializeGpu_Click(object sender, RoutedEventArgs e)
    {
        var resultMessage = string.Empty;
        await RunMapLearningActionAsync(async () =>
        {
            _status.Text = "正在初始化 GPU 训练运行时…";
            _gpuSidecarState.Text = "GPU 加速：正在初始化 CUDA 与 cuDNN…";
            var result = await _runtime.InitializeMapLearningGpuAsync();
            resultMessage = result.Message;
        });
        if (!string.IsNullOrWhiteSpace(resultMessage))
        {
            _status.Text = resultMessage;
            _gpuSidecarState.Text = "GPU 加速：" + resultMessage;
        }
    }

    private async void ExportMapTraining_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(
            ((App)Application.Current).MainWindow.AppWindow.Id)
        {
            SuggestedFileName = $"IDVB-map-training-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("ZIP 训练包", new List<string> { ".zip" });
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        await RunMapLearningActionAsync(async () =>
        {
            var path = await _runtime.ExportMapTrainingDataAsync(file.Path);
            _status.Text = $"脱敏训练包已导出：{path}";
        });
    }

    private async void ClearMapTraining_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清理训练样本？",
            Content = "将删除训练样本；固定验证样本和模型版本会保留。",
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await RunMapLearningActionAsync(() =>
            _runtime.ClearMapTrainingSamplesAsync());
    }

    private async void MapModelHistory_Click(object sender, RoutedEventArgs e)
    {
        await RunMapLearningActionAsync(async () =>
        {
            var versions = await _runtime.GetMapModelVersionsAsync();
            var selector = new ComboBox
            {
                MinWidth = 440,
                ItemsSource = versions,
                DisplayMemberPath = nameof(MapModelVersionInfo.Version),
                SelectedIndex = versions.Count > 0 ? 0 : -1
            };
            var pin = new CheckBox { Content = "固定此版本，不参与自动清理" };
            selector.SelectionChanged += (_, _) =>
                pin.IsChecked = (selector.SelectedItem as MapModelVersionInfo)?.IsPinned;
            if (selector.SelectedItem is MapModelVersionInfo initial)
                pin.IsChecked = initial.IsPinned;
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(selector);
            content.Children.Add(pin);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "模型历史与恢复",
                Content = content,
                PrimaryButtonText = "恢复所选版本",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = versions.Count > 0
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && selector.SelectedItem is MapModelVersionInfo version)
            {
                await _runtime.SetMapModelVersionPinnedAsync(
                    version.Version,
                    pin.IsChecked is true);
                await _runtime.RestoreMapModelVersionAsync(version.Version);
                _status.Text = $"已恢复模型版本 {version.Version}。";
            }
        });
    }

    private async Task RunMapLearningActionAsync(Func<Task> action)
    {
        try
        {
            SetMapLearningControlsEnabled(false);
            await action();
            Refresh();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        finally
        {
            SetMapLearningControlsEnabled(true);
        }
    }

    private void SetMapLearningControlsEnabled(bool enabled)
    {
        _candidateDecisionMode.IsEnabled = enabled;
        _continuousLearningToggle.IsEnabled = enabled;
        _automaticModelTrainingToggle.IsEnabled = enabled
            && _continuousLearningToggle.IsOn;
        _trainMapModelButton.IsEnabled = enabled;
        _initializeGpuButton.IsEnabled = enabled;
        _exportMapTrainingButton.IsEnabled = enabled;
        _clearMapTrainingButton.IsEnabled = enabled;
        _mapModelHistoryButton.IsEnabled = enabled;
    }

    private void UpdateMapLearningProgress(MapLearningStatus status)
    {
        _mapLearningState.Text = FormatMapLearningStatus(status);
        _mapLearningProgress.Visibility = status.IsTraining
            ? Visibility.Visible
            : Visibility.Collapsed;
        _mapLearningProgressText.Visibility = status.IsTraining
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!status.IsTraining)
            return;
        var phase = status.TrainingPhase switch
        {
            MapLearningTrainingPhase.PreparingSamples => "正在准备与预处理样本",
            MapLearningTrainingPhase.Training => "正在训练空间匹配模型",
            MapLearningTrainingPhase.Evaluating => "正在验证模型",
            MapLearningTrainingPhase.Saving => "正在保存不可变模型版本",
            MapLearningTrainingPhase.Reloading => "正在重载并校验模型",
            _ => "正在启动训练"
        };
        var hasTotal = status.TrainingProgressTotal > 0;
        _mapLearningProgress.IsIndeterminate = !hasTotal;
        _mapLearningProgress.Value = hasTotal
            ? (double)status.TrainingProgressCurrent
                / status.TrainingProgressTotal
            : 0d;
        var detail = status.TrainingPhase == MapLearningTrainingPhase.Training
            ? $" · Epoch {status.TrainingEpoch}/{status.TrainingEpochCount}"
                + $" · Batch {status.TrainingBatch}/{status.TrainingBatchCount}"
            : hasTotal
                ? $" · {status.TrainingProgressCurrent}/{status.TrainingProgressTotal}"
                : string.Empty;
        _mapLearningProgressText.Text = phase + detail;
    }

    private static string FormatMapLearningStatus(MapLearningStatus status)
    {
        if (string.Equals(status.ComputeDevice, "样本提供模式",
                StringComparison.Ordinal))
        {
            return $"已保存脱敏样本：{status.HumanSelectionCount} 场"
                + $" · 地图身份 {status.DistinctMapCount} 个"
                + $" · 独立验证 {status.ValidationMatchCount} 场"
                + (status.LegacyHumanSelectionCount > 0
                    ? $"\n旧格式样本：{status.LegacyHumanSelectionCount} 场"
                    : string.Empty)
                + "\n主程序仅提供样本，不执行模型训练或推理。";
        }
        var version = string.IsNullOrWhiteSpace(status.CurrentVersion)
            ? "无"
            : status.CurrentVersion;
        var state = status.IsTraining
            ? "训练中"
            : status.IsAvailable
                ? status.IsQualified
                    ? "稳定模型（已验证）"
                    : "实验模型（训练完成，未晋级）"
                : "不可用";
        var result = $"当前版本：{version} · 状态：{state}"
            + $" · 计算设备：{status.ComputeDevice}"
            + $"\n训练数据：人工对局 {status.HumanSelectionCount}/{status.RequiredHumanSelectionCount}"
            + $" · 地图身份 {status.DistinctMapCount}/{status.RequiredDistinctMapCount}"
            + $" · 独立验证 {status.ValidationMatchCount}/{status.RequiredValidationMatchCount}";
        if (status.LegacyHumanSelectionCount > 0)
        {
            result += $"\n旧契约样本：{status.LegacyHumanSelectionCount} 场"
                + "（保留但不参与空间模型训练）";
        }
        if (status.MigratedLegacyHumanSelectionCount > 0)
        {
            result += $"\n已无损迁移旧数据：{status.MigratedLegacyHumanSelectionCount} 场"
                + "（原清单仍保留，现使用完整楼层参与空间训练）";
        }
        result += status.ValidationMatchCount
                < status.RequiredValidationMatchCount
            ? "\n验证指标：等待至少 4 场独立验证对局；当前准确率不用于晋级。"
            : $"\n验证指标：Top-1 {status.ValidationAccuracy:P1}"
                + $" · 传统基线 {status.TraditionalValidationAccuracy:P1}"
                + $" · 校准误差 {status.CalibrationError:F3}";
        result += $"\n空间验证：可信区域 "
            + $"{status.TrustedSpatialValidationCount}/"
            + $"{status.RequiredTrustedSpatialValidationCount}"
            + (status.TrustedSpatialValidationCount == 0
                ? " · 尚不能验证楼层内定位"
                : $" · 区域命中率 {status.SpatialValidationAccuracy:P1}"
                    + $" · 平均误差 {status.SpatialMeanError:F3}");
        if (status.LastTrainingTime is { } trained)
            result += $" · 最近训练 {trained.LocalDateTime:g}";
        if (!string.IsNullOrWhiteSpace(status.LastTrainingComputeDevice))
            result += $" · 训练设备 {status.LastTrainingComputeDevice}";
        if (!string.IsNullOrWhiteSpace(status.LastFailureReason))
            result += $"\n训练失败：{status.LastFailureReason}";
        if (!string.IsNullOrWhiteSpace(status.PromotionBlockReason))
            result += $"\n{status.PromotionBlockReason}";
        if (!string.IsNullOrWhiteSpace(status.LastRollbackReason))
            result += $"\n模型回退：{status.LastRollbackReason}";
        if (!string.IsNullOrWhiteSpace(status.ComputeFallbackReason))
            result += $"\n计算设备回退：{status.ComputeFallbackReason}";
        return result;
    }
}
