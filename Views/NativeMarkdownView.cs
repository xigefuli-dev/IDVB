using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.Text;

namespace IDVBuff.Views;

/// <summary>
/// 纯原生 WinUI 3 Markdown 阅读控件。
/// 零外部进程、零 Native DLL 依赖、毫秒级渲染、深浅主题自动适配。
/// 完全免疫 WebView2 运行时缺失或损坏问题，彻底杜绝崩溃与系统卡死。
/// </summary>
public sealed partial class NativeMarkdownView : Grid
{
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _container;
    private string _currentMarkdown = string.Empty;

    public NativeMarkdownView()
    {
        _container = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(24, 20, 28, 28),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _container,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        Children.Add(_scrollViewer);
        ActualThemeChanged += (_, _) => UpdateThemeBrushes();
    }

    public void SetMarkdown(string? markdown)
    {
        _currentMarkdown = markdown ?? string.Empty;
        Render();
    }

    public void Clear()
    {
        _currentMarkdown = string.Empty;
        _container.Children.Clear();
    }

    private void Render()
    {
        _container.Children.Clear();

        if (string.IsNullOrWhiteSpace(_currentMarkdown))
            return;

        var normalized = _currentMarkdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        int index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];

            // 1. 代码块
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var codeLines = new List<string>();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[index]);
                    index++;
                }
                if (index < lines.Length) index++; // 跳过结束的 ```

                var codeBlock = CreateCodeBlock(string.Join("\n", codeLines));
                _container.Children.Add(codeBlock);
                continue;
            }

            // 2. 空行跳过
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            var trimmed = line.Trim();

            // 3. 分割线
            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                _container.Children.Add(CreateDivider());
                index++;
                continue;
            }

            // 4. 标题与伪标题（如【概要】、【新功能与特性】）
            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                _container.Children.Add(CreateHeading(trimmed[4..], 16, FontWeights.SemiBold, 14, 4));
                index++;
                continue;
            }
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                _container.Children.Add(CreateHeading(trimmed[3..], 18, FontWeights.SemiBold, 18, 6));
                index++;
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                _container.Children.Add(CreateHeading(trimmed[2..], 22, FontWeights.Bold, 22, 8));
                index++;
                continue;
            }
            if (trimmed.StartsWith('【') && trimmed.EndsWith('】') && trimmed.Length <= 20)
            {
                _container.Children.Add(CreateSectionHeading(trimmed));
                index++;
                continue;
            }

            // 5. 引用块
            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                var quoteLines = new List<string>();
                while (index < lines.Length && (lines[index].Trim().StartsWith('>') || (quoteLines.Count > 0 && !string.IsNullOrWhiteSpace(lines[index]))))
                {
                    var ql = lines[index].Trim();
                    if (ql.StartsWith("> ", StringComparison.Ordinal))
                        quoteLines.Add(ql[2..]);
                    else if (ql.StartsWith('>'))
                        quoteLines.Add(ql[1..].TrimStart());
                    else
                        quoteLines.Add(ql);
                    index++;
                }

                _container.Children.Add(CreateQuoteBlock(string.Join(" ", quoteLines)));
                continue;
            }

            // 6. 无序列表项
            if (IsListItem(line, out var indentLevel, out var listContent))
            {
                _container.Children.Add(CreateListItem(listContent, indentLevel));
                index++;
                continue;
            }

            // 7. 图片
            var imgMatch = Regex.Match(trimmed, @"^!\[(.*?)\]\((.*?)\)$");
            if (imgMatch.Success)
            {
                _container.Children.Add(CreateImageBlock(imgMatch.Groups[1].Value, imgMatch.Groups[2].Value));
                index++;
                continue;
            }

            // 8. 普通段落
            var paragraphLines = new List<string> { trimmed };
            index++;
            while (index < lines.Length)
            {
                var next = lines[index];
                if (string.IsNullOrWhiteSpace(next) ||
                    next.TrimStart().StartsWith('#') ||
                    next.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
                    next.TrimStart().StartsWith('>') ||
                    next.TrimStart().StartsWith("---", StringComparison.Ordinal) ||
                    (next.TrimStart().StartsWith('【') && next.TrimEnd().EndsWith('】')) ||
                    IsListItem(next, out _, out _))
                {
                    break;
                }
                paragraphLines.Add(next.Trim());
                index++;
            }

            _container.Children.Add(CreateParagraph(string.Join(" ", paragraphLines)));
        }
    }

    private void UpdateThemeBrushes()
    {
        // 重新渲染以刷新 FluentTheme.Brush 笔刷颜色
        Render();
    }
}
