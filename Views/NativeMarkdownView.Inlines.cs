using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;

namespace IDVBuff.Views;

public sealed partial class NativeMarkdownView
{
    private static bool IsListItem(string rawLine, out int indentLevel, out string content)
    {
        indentLevel = 0;
        content = string.Empty;

        int spaces = 0;
        while (spaces < rawLine.Length && rawLine[spaces] == ' ')
            spaces++;

        indentLevel = spaces / 2;
        var remaining = rawLine[spaces..];

        if (remaining.StartsWith("- ", StringComparison.Ordinal) ||
            remaining.StartsWith("* ", StringComparison.Ordinal) ||
            remaining.StartsWith("• ", StringComparison.Ordinal))
        {
            content = remaining[2..];
            return true;
        }

        // 数字列表：1. 2.
        var numMatch = Regex.Match(remaining, @"^\d+\.\s+(.*)$");
        if (numMatch.Success)
        {
            content = numMatch.Groups[1].Value;
            return true;
        }

        return false;
    }

    private static TextBlock CreateHeading(string text, double fontSize, FontWeight weight, double topMargin, double bottomMargin)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            Margin = new Thickness(0, topMargin, 0, bottomMargin),
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };
        return block;
    }

    private static FrameworkElement CreateSectionHeading(string text)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 16, 0, 4)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Width = 4,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = FluentTheme.Brush("AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

        var title = new TextBlock
        {
            Text = text.Trim('【', '】'),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        return grid;
    }

    private static Border CreateDivider()
    {
        return new Border
        {
            Height = 1,
            Background = FluentTheme.Brush("DividerStrokeColorDefaultBrush"),
            Margin = new Thickness(0, 8, 0, 8)
        };
    }

    private static FrameworkElement CreateListItem(string content, int indentLevel)
    {
        var grid = new Grid
        {
            Margin = new Thickness(indentLevel * 18, 2, 0, 2),
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bullet = new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 8, 0, 0)
        };
        Grid.SetColumn(bullet, 0);
        grid.Children.Add(bullet);

        var textBlock = CreateFormattedTextBlock(content, 14, 1.4);
        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(textBlock);

        return grid;
    }

    private static FrameworkElement CreateQuoteBlock(string content)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = FluentTheme.Brush("AccentFillColorDefaultBrush"),
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 4, 0, 4)
        };

        var textBlock = CreateFormattedTextBlock(content, 13.5, 1.4);
        textBlock.Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush");
        border.Child = textBlock;
        return border;
    }

    private static FrameworkElement CreateCodeBlock(string code)
    {
        var border = new Border
        {
            Background = FluentTheme.Brush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 6, 0, 6)
        };

        var textBlock = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Courier New"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };

        border.Child = textBlock;
        return border;
    }

    private static FrameworkElement CreateImageBlock(string alt, string url)
    {
        var container = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8)
        };

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                MaxWidth = 640,
                MaxHeight = 400
            };
            image.Source = new BitmapImage(uri);
            container.Children.Add(image);
        }

        if (!string.IsNullOrWhiteSpace(alt))
        {
            container.Children.Add(new TextBlock
            {
                Text = alt,
                FontSize = 11,
                Foreground = FluentTheme.Brush("TextFillColorTertiaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        return container;
    }

    private static FrameworkElement CreateParagraph(string content)
    {
        var textBlock = CreateFormattedTextBlock(content, 14, 1.5);
        textBlock.Margin = new Thickness(0, 2, 0, 2);
        return textBlock;
    }

    private static TextBlock CreateFormattedTextBlock(string markdown, double fontSize, double lineSpacingRatio)
    {
        var textBlock = new TextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };

        // 解析行内样式：**加粗**、`代码`、[链接](url)、普通文本
        var tokens = TokenizeInlineMarkdown(markdown);
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case InlineKind.Bold:
                    textBlock.Inlines.Add(new Run
                    {
                        Text = token.Text,
                        FontWeight = FontWeights.Bold
                    });
                    break;

                case InlineKind.Italic:
                    textBlock.Inlines.Add(new Run
                    {
                        Text = token.Text,
                        FontStyle = Windows.UI.Text.FontStyle.Italic
                    });
                    break;

                case InlineKind.Code:
                    var codeContainer = new InlineUIContainer();
                    var codeBorder = new Border
                    {
                        Background = FluentTheme.Brush("CardBackgroundFillColorSecondaryBrush"),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(1, 0, 1, 0),
                        Child = new TextBlock
                        {
                            Text = token.Text,
                            FontFamily = new FontFamily("Consolas, Cascadia Code"),
                            FontSize = fontSize * 0.9,
                            Foreground = FluentTheme.Brush("AccentTextFillColorPrimaryBrush")
                        }
                    };
                    codeContainer.Child = codeBorder;
                    textBlock.Inlines.Add(codeContainer);
                    break;

                case InlineKind.Link:
                    var hyperlink = new Hyperlink();
                    hyperlink.Inlines.Add(new Run { Text = token.Text });
                    var targetUrl = token.Extra;
                    hyperlink.Click += (_, _) =>
                    {
                        if (!string.IsNullOrEmpty(targetUrl) &&
                            (targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[NativeMarkdownView] 打开链接异常: {ex.Message}");
                            }
                        }
                    };
                    textBlock.Inlines.Add(hyperlink);
                    break;

                case InlineKind.Text:
                default:
                    textBlock.Inlines.Add(new Run { Text = token.Text });
                    break;
            }
        }

        return textBlock;
    }

    private enum InlineKind { Text, Bold, Italic, Code, Link }
    private sealed record InlineToken(InlineKind Kind, string Text, string? Extra = null);

    private static List<InlineToken> TokenizeInlineMarkdown(string input)
    {
        var result = new List<InlineToken>();
        if (string.IsNullOrEmpty(input)) return result;

        // 匹配 **bold**、`code`、[link](url)、*italic*
        var pattern = @"(\*\*(?<bold>[^*]+)\*\*)|(`(?<code>[^`]+)`)|(\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)]+)\))|(\*(?<italic>[^*]+)\*)";
        var matches = Regex.Matches(input, pattern);

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                result.Add(new InlineToken(InlineKind.Text, input[lastIndex..match.Index]));
            }

            if (match.Groups["bold"].Success)
            {
                result.Add(new InlineToken(InlineKind.Bold, match.Groups["bold"].Value));
            }
            else if (match.Groups["code"].Success)
            {
                result.Add(new InlineToken(InlineKind.Code, match.Groups["code"].Value));
            }
            else if (match.Groups["linkText"].Success)
            {
                result.Add(new InlineToken(InlineKind.Link, match.Groups["linkText"].Value, match.Groups["linkUrl"].Value));
            }
            else if (match.Groups["italic"].Success)
            {
                result.Add(new InlineToken(InlineKind.Italic, match.Groups["italic"].Value));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
        {
            result.Add(new InlineToken(InlineKind.Text, input[lastIndex..]));
        }

        return result;
    }
}
