using System.Text.Json;
using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class ShoppingSearchDiscoveryTests
{
    private static readonly NexusSearchReport SearchReport = new(
        "ноутбук",
        "Сравнение найденных страниц",
        [
            new NexusSearchItem("Ноутбук Nexus 14", "https://shop.example.com/catalog/nexus-14",
                "Цена 19 990 ₽. Экран 14 дюймов.", "В наличии", 9.4),
            new NexusSearchItem("Другой магазин", "https://outside.example.net/item/42",
                "24 500 руб.", "Описание товара", 8.2)
        ],
        "Источники обнаружены поисковой системой и обработаны локально.");

    [Fact]
    public void SiteFallbackKeepsOnlyRequestedDomainAndObservablePrice()
    {
        var json = NexusSearchService.BuildShoppingCardsFromSearchReport(SearchReport, "example.com");
        using var document = JsonDocument.Parse(json);

        var card = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("Ноутбук Nexus 14", card.GetProperty("name").GetString());
        Assert.Equal("19 990 ₽", card.GetProperty("price").GetString());
        Assert.Equal("", card.GetProperty("rating").GetString());
        Assert.Equal("https://shop.example.com/catalog/nexus-14", card.GetProperty("url").GetString());
    }

    [Fact]
    public void GlobalSearchCanCompareResultsFromDifferentSites()
    {
        var json = NexusSearchService.BuildShoppingCardsFromSearchReport(SearchReport, null);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.All(document.RootElement.EnumerateArray(), card =>
            Assert.Equal("search-engine", card.GetProperty("source").GetString()));
    }

    [Fact]
    public void InvalidSiteConstraintFailsClosedInsteadOfBecomingGlobalSearch()
    {
        Assert.Throws<ArgumentException>(() =>
            NexusSearchService.BuildShoppingCardsFromSearchReport(SearchReport, "not a host/value"));
    }
}
