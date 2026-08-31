using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace IDVBuff.Views;
public sealed partial class MapListPage : UserControl
{

    private static Button CreateSecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Background = FluentTheme.Brush("ControlFillColorDefaultBrush"),
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            BorderBrush = FluentTheme.Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            MinWidth = 98,
            MinHeight = 38,
            Padding = new Thickness(16, 6, 16, 6),
            CornerRadius = new CornerRadius(7)
        };
        AttachHoverFeedback(button);
        return button;
    }

    private static BitmapImage CreateBitmap(string path, int? decodePixelWidth = null) => new()
    {
        CreateOptions = BitmapCreateOptions.None,
        DecodePixelWidth = decodePixelWidth ?? 0,
        UriSource = new Uri(path)
    };

    private void PlayWorkflowEnterAnimation()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_workflowHost, true);
        var visual = ElementCompositionPreview.GetElementVisual(_workflowHost);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 0;

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, CreateMainEase(visual));
        opacity.Duration = WorkflowEnterDuration;

        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, new Vector3(0, 14, 0));
        translation.InsertKeyFrame(1f, Vector3.Zero, CreateMainEase(visual));
        translation.Duration = WorkflowEnterDuration;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    private static void PlayDetailTriggerFeedback(UIElement trigger)
    {
        var visual = ElementCompositionPreview.GetElementVisual(trigger);
        if (trigger is FrameworkElement element)
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        visual.Scale = new Vector3(0.985f, 0.985f, 1);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, Vector3.One, CreateDetailEase(visual));
        animation.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Scale", animation);
    }

    private static void AttachHoverFeedback(UIElement target)
    {
        target.PointerEntered += (_, _) => PlayHoverFeedback(target, 1.01f, TimeSpan.FromMilliseconds(150));
        target.PointerExited += (_, _) => PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(100));
    }

    private static void AttachCardInteractionFeedback(UIElement target)
    {
        var isPressed = false;
        var pressCanceled = false;
        var isReleasingCapture = false;

        target.PointerEntered += (_, _) =>
        {
            if (!isPressed)
                PlayHoverFeedback(target, 1.01f, TimeSpan.FromMilliseconds(150));
        };
        target.PointerExited += (_, _) =>
        {
            if (isPressed)
                pressCanceled = true;
            PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(100));
        };
        target.PointerPressed += (_, e) =>
        {
            isPressed = true;
            pressCanceled = false;
            target.CapturePointer(e.Pointer);
            PlayHoverFeedback(target, 0.975f, TimeSpan.FromMilliseconds(80));
        };
        target.PointerMoved += (_, e) =>
        {
            if (!isPressed || target is not FrameworkElement element)
                return;
            var position = e.GetCurrentPoint(target).Position;
            var isInside = position.X >= 0d
                && position.Y >= 0d
                && position.X <= element.ActualWidth
                && position.Y <= element.ActualHeight;
            if (pressCanceled == !isInside)
                return;
            pressCanceled = !isInside;
            PlayHoverFeedback(
                target,
                isInside ? 0.975f : 1f,
                TimeSpan.FromMilliseconds(isInside ? 80 : 110));
        };
        target.PointerReleased += (_, e) =>
        {
            if (!isPressed)
                return;
            isPressed = false;
            isReleasingCapture = true;
            target.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;
            if (pressCanceled)
                PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(110));
            else
                PlayDetailTriggerFeedback(target);
        };
        target.PointerCanceled += (_, e) =>
        {
            isPressed = false;
            pressCanceled = true;
            isReleasingCapture = true;
            target.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;
            PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(110));
        };
        target.PointerCaptureLost += (_, _) =>
        {
            if (isReleasingCapture)
                return;
            isPressed = false;
            pressCanceled = true;
            PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(110));
        };
    }

    private static void PlayHoverFeedback(UIElement target, float scale, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        if (target is FrameworkElement element)
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1), CreateDetailEase(visual));
        animation.Duration = duration;
        visual.StartAnimation("Scale", animation);
    }

    private static Microsoft.UI.Composition.CubicBezierEasingFunction CreateMainEase(Microsoft.UI.Composition.Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

    private static Microsoft.UI.Composition.CubicBezierEasingFunction CreateDetailEase(Microsoft.UI.Composition.Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));

    private async Task ShowRenameClassDialogAsync()
    {
        var currentClass = _selectedClass;
        var textBox = new TextBox
        {
            Text = currentClass,
            PlaceholderText = "输入新的地图类名称"
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名地图类",
            Content = textBox,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        textBox.TextChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(textBox.Text) && !string.Equals(textBox.Text, currentClass, StringComparison.OrdinalIgnoreCase);
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var newName = textBox.Text.Trim();
        if (string.Equals(newName, currentClass, StringComparison.OrdinalIgnoreCase))
            return;

        await RenameClassAsync(currentClass, newName);
        _selectedClass = newName;
        await ShowListAsync();
    }

    private async Task RenameClassAsync(string oldName, string newName)
    {
        await _repository.RenameClassAsync(oldName, newName);
    }

    private async Task ReorderCurrentClassAsync()
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重新排序当前地图类",
            Content = $"将“{_selectedClass}”中的每个变体组合优先归拢为连续块，并按稳定顺序重新编号。此操作会清除当前地图类的自定义地图名称，但不会改变地图 Guid、资产或缓存键。\n\n迁移备份保存在：\n{_repository.VariantMigrationBackupRoot}",
            PrimaryButtonText = "确认重新排序",
            CloseButtonText = "取消"
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        await _repository.ReorderClassAsync(_selectedClass);
        await ShowListAsync();
    }
}
