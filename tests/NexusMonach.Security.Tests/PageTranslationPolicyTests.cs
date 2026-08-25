using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class PageTranslationPolicyTests
{
    [Fact]
    public void InteractiveBoundary_IncludesMenusButtonsAndTabs()
    {
        Assert.Contains("a[href]", PageTranslationPolicy.InteractiveSelector, StringComparison.Ordinal);
        Assert.Contains("button", PageTranslationPolicy.InteractiveSelector, StringComparison.Ordinal);
        Assert.Contains("menuitem", PageTranslationPolicy.InteractiveSelector, StringComparison.Ordinal);
        Assert.Contains("tab", PageTranslationPolicy.InteractiveSelector, StringComparison.Ordinal);
        Assert.Contains("combobox", PageTranslationPolicy.InteractiveSelector, StringComparison.Ordinal);
        Assert.Contains("summary", PageTranslationPolicy.InteractiveSelector, StringComparison.Ordinal);
        Assert.Contains("placeholder", PageTranslationPolicy.TranslatableAttributes);
        Assert.Contains("aria-label", PageTranslationPolicy.TranslatableAttributes);
    }

    [Fact]
    public void ArticleBoundary_ExcludesNavigationFormsAndEditableFields()
    {
        Assert.Contains("nav", PageTranslationPolicy.ArticleExclusionSelector, StringComparison.Ordinal);
        Assert.Contains("form", PageTranslationPolicy.ArticleExclusionSelector, StringComparison.Ordinal);
        Assert.Contains("input", PageTranslationPolicy.ArticleExclusionSelector, StringComparison.Ordinal);
        Assert.Contains("textarea", PageTranslationPolicy.ArticleExclusionSelector, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("submit", true)]
    [InlineData("button", true)]
    [InlineData("reset", true)]
    [InlineData("text", false)]
    [InlineData("email", false)]
    [InlineData("password", false)]
    public void InputValueBoundary_AllowsOnlyButtonLabels(string inputType, bool expected)
    {
        Assert.Equal(expected, PageTranslationPolicy.CanTranslateInputValue(inputType));
        Assert.False(PageTranslationPolicy.CanReadUserValue(inputType));
    }
}
