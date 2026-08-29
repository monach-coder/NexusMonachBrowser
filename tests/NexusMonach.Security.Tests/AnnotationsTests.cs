using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Система пометок: структура Markdown-экспорта (дерево сайт → страница →
/// запись), подписи цветов и чистая логика строителя.
/// </summary>
public sealed class AnnotationsTests
{
    private static PageAnnotation Highlight(string url, string quote, HighlightColor color, string note = "") => new()
    {
        Kind = AnnotationKind.Highlight,
        Url = url,
        Quote = quote,
        Color = color,
        Note = note,
        PageTitle = "Страница " + url
    };

    private static PageAnnotation Fragment(string url, double position, double duration) => new()
    {
        Kind = AnnotationKind.VideoFragment,
        Url = url,
        MediaPath = "notes-media/fragment-test.webm",
        VideoPositionSeconds = position,
        DurationSeconds = duration,
        PageTitle = "Видео"
    };

    [Fact]
    public void Markdown_GroupsBySiteAndPage()
    {
        var markdown = AnnotationsService.BuildMarkdown(new[]
        {
            Highlight("https://a.com/x", "первая", HighlightColor.Yellow),
            Highlight("https://a.com/y", "вторая", HighlightColor.Green),
            Highlight("https://b.com/z", "третья", HighlightColor.Red)
        });
        // Сайт идёт заголовком второго уровня, страница — третьего.
        Assert.Contains("## a.com", markdown, StringComparison.Ordinal);
        Assert.Contains("## b.com", markdown, StringComparison.Ordinal);
        Assert.Contains("### [Страница https://a.com/x](https://a.com/x)", markdown, StringComparison.Ordinal);
        // Заголовок документа один.
        Assert.Equal(1, Count(markdown, "# Заметки Nexus Monach"));
    }

    [Fact]
    public void Markdown_QuoteBecomesBlockquote_WithColorLabel()
    {
        var markdown = AnnotationsService.BuildMarkdown(
            [Highlight("https://a.com/x", "важная мысль", HighlightColor.Green)]);
        Assert.Contains("> важная мысль", markdown, StringComparison.Ordinal);
        Assert.Contains("выделение: зелёный", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_MultilineQuote_StaysInsideBlockquote()
    {
        var markdown = AnnotationsService.BuildMarkdown(
            [Highlight("https://a.com/x", "строка один\nстрока два", HighlightColor.Blue)]);
        Assert.Contains("> строка один\n> строка два", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_VideoFragment_HasTimecodeAndMediaLink()
    {
        var markdown = AnnotationsService.BuildMarkdown(
            [Fragment("https://a.com/video", 95, 30)]);
        Assert.Contains("🎬 **Видео-фрагмент**", markdown, StringComparison.Ordinal);
        Assert.Contains("01:35 (+00:30)", markdown, StringComparison.Ordinal);
        Assert.Contains("./notes-media/fragment-test.webm", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_NoteCarriesText()
    {
        var annotation = new PageAnnotation
        {
            Kind = AnnotationKind.Note, Url = "https://a.com/x",
            Quote = "цитата", Note = "проверить источник"
        };
        var markdown = AnnotationsService.BuildMarkdown([annotation]);
        Assert.Contains("📝 **проверить источник**", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HighlightColor.Yellow, "жёлтый")]
    [InlineData(HighlightColor.Green, "зелёный")]
    [InlineData(HighlightColor.Red, "красный")]
    [InlineData(HighlightColor.Blue, "синий")]
    public void ColorNames_Russian(HighlightColor color, string expected)
    {
        Assert.Equal(expected, AnnotationsService.ColorName(color));
    }

    [Fact]
    public void HighlightsScript_WrapsPayloadForInjection()
    {
        var script = AnnotationsBridge.HighlightsScript(
            [Highlight("https://a.com/x", "цитата", HighlightColor.Yellow, "заметка")]);
        Assert.StartsWith("window.nexusApplyHighlights?.(", script, StringComparison.Ordinal);
        Assert.EndsWith(");", script, StringComparison.Ordinal);
        Assert.Contains("\"quote\":\"цитата\"", script, StringComparison.Ordinal);
        Assert.Contains("\"color\":\"Yellow\"", script, StringComparison.Ordinal);
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
