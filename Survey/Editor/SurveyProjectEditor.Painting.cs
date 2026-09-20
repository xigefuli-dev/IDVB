namespace IDVBuff.Survey.Editor.WinUI;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using IDVBuff.Survey.Domain;

public sealed partial class SurveyProjectEditor
{
    private TextBox? _paintHexBox;
    private ColorPicker? _paintPicker;
    private Border? _paintColorPreview;

    private void SetPaintColor(SurveyColor color)
    {
        _paintColor = color;
        if (_paintHexBox is not null) _paintHexBox.Text = color.ToHex();
        if (_paintPicker is not null) _paintPicker.Color = Color.FromArgb(255, color.R, color.G, color.B);
        if (_paintColorPreview is not null)
            _paintColorPreview.Background = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B));
        _canvas.SetPaintColor(color);
    }

    private async Task SamplePaintColorAsync(SurveyLayerPixelSampleEventArgs e)
    {
        try
        {
            var sampled = await _session.SampleCompositedPixelAsync(
                _floorKey, e.WorldPoint, _lifetimeCancellation.Token);
            if (sampled is null)
            {
                SetStatus("无法读取当前画面像素。", true);
                return;
            }
            SetPaintColor(new SurveyColor(sampled.R, sampled.G, sampled.B));
            SetStatus($"已取色 {_paintColor.ToHex()}。", false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception) { SetStatus($"取色失败：{exception.Message}", true); }
    }

    private async void Canvas_ColorStrokeCommitted(object? sender, SurveyColorStrokeEventArgs e)
    {
        if (_disposed) return;
        if (_layers.PrimaryLayerId != e.LayerId) { SetStatus("画笔只作用于当前主选图层。", true); return; }
        var result = await _session.ApplyColorBrushAsync(e.LayerId, e.Points, _brushSize, _brushShape,
            _paintColor, _lifetimeCancellation.Token);
        var item = result?.Items.FirstOrDefault();
        SetStatus(item?.Succeeded is true ? $"画笔已应用 {_paintColor.ToHex()}。" : item?.Message ?? "画笔未修改图层。", item?.Succeeded is not true);
    }

    private async void Canvas_ColorFillRequested(object? sender, SurveyColorFillEventArgs e)
    {
        if (_disposed) return;
        if (_layers.PrimaryLayerId != e.LayerId) { SetStatus("颜料桶只作用于当前主选图层。", true); return; }
        var result = await _session.ApplyColorFillAsync(e.LayerId, e.PixelX, e.PixelY, _fillTolerance,
            _paintColor, _lifetimeCancellation.Token);
        var item = result?.Items.FirstOrDefault();
        SetStatus(item?.Succeeded is true ? $"已填充 {_paintColor.ToHex()}。" : item?.Message ?? "颜料桶未修改图层。", item?.Succeeded is not true);
    }
}
