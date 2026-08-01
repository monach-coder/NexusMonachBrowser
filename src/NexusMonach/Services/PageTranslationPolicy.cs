namespace NexusMonach.Services;

/// <summary>
/// Единая проверяемая граница между озвучиваемой статьёй и теми элементами
/// интерфейса сайта, которые разрешено переводить непосредственно в DOM.
/// </summary>
internal static class PageTranslationPolicy
{
    public const string MainContentSelector =
        "article,main,[role=\"main\"],[itemprop=\"articleBody\"]," +
        "[class*=\"article-body\" i],[class*=\"article-content\" i]," +
        "[class*=\"post-content\" i]";

    public const string ArticleExclusionSelector =
        "script,style,noscript,nav,header,footer,aside,[role=\"navigation\"]," +
        "[role=\"complementary\"],[role=\"dialog\"],[class*=\"comment\" i]," +
        "[class*=\"advert\" i],[class*=\"sidebar\" i],form,[role=\"form\"]," +
        "input,textarea,select,option,button,label,fieldset,code,pre,svg,canvas," +
        "[contenteditable=\"true\"],[data-nexus-translation-ui]";

    public const string InteractiveSelector =
        "input:not([type=\"hidden\"]),textarea,select,button,a[href]," +
        "[role=\"navigation\"] a,[role=\"button\"],[role=\"link\"]," +
        "[role=\"menuitem\"],[role=\"tab\"],[role=\"checkbox\"],[role=\"radio\"]," +
        "[aria-haspopup=\"menu\"]";

    public static readonly string[] TranslatableInputValueTypes = ["submit", "button", "reset"];

    public static bool CanTranslateInputValue(string? inputType) =>
        TranslatableInputValueTypes.Contains(inputType ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
}
