using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Тихий Следопыт: терминный скоринг, приор из графа знаний, обучение
/// на выборах пользователя и пороги «знакомого» запроса. Всё чистые
/// функции — сетевая часть наблюдателя им не покрывается.
/// </summary>
public sealed class SledopytSilentTests
{
    private static readonly Dictionary<string, int> NoVisits = new();

    [Fact]
    public void BaseScore_TitleMatch_BeatsUrlOnly()
    {
        var titleMatch = SledopytRelevance.BaseScore("разработка игр", "Разработка игр на Rust", "https://a.com/x");
        var urlOnly = SledopytRelevance.BaseScore("разработка игр", "Статья", "https://a.com/разработка-игр");
        Assert.True(titleMatch > urlOnly);
    }

    [Fact]
    public void BaseScore_FullTitleCoverage_GetsBonus()
    {
        var full = SledopytRelevance.BaseScore("rust async", "Rust Async Guide", "https://a.com");
        var partial = SledopytRelevance.BaseScore("rust async", "Rust Book", "https://a.com");
        Assert.True(full > partial * 1.5);
    }

    [Fact]
    public void BaseScore_EmptyQuery_IsZero()
    {
        Assert.Equal(0, SledopytRelevance.BaseScore("", "title", "https://a.com"));
        Assert.Equal(0, SledopytRelevance.BaseScore("  ", "title", "https://a.com"));
    }

    [Fact]
    public void FinalScore_KnownHost_GetsGraphBoost()
    {
        var visits = new Dictionary<string, int> { ["docs.rust-lang.org"] = 10 };
        var known = SledopytRelevance.FinalScore(2.0, "https://docs.rust-lang.org/x", "async rust", visits, new());
        var unknown = SledopytRelevance.FinalScore(2.0, "https://random.net/x", "async rust", NoVisits, new());
        Assert.True(known > unknown + 1.0);
    }

    [Fact]
    public void FinalScore_LearnedChoice_AddsWeight()
    {
        var learning = new SledopytRelevance.LearningModel();
        learning.Record("как приготовить борщ", "povarenok.ru");
        var learned = SledopytRelevance.FinalScore(1.5, "https://povarenok.ru/recipe", "как приготовить борщ", NoVisits, learning);
        var fresh = SledopytRelevance.FinalScore(1.5, "https://povarenok.ru/recipe", "как приготовить борщ", NoVisits, new());
        Assert.True(learned > fresh);
    }

    [Fact]
    public void FamiliarHost_RaisesAnnounceThreshold()
    {
        var visits = new Dictionary<string, int> { ["habr.com"] = 4 };
        Assert.True(SledopytRelevance.IsFamiliar("rust новости", "https://habr.com/x", visits, new()));
        Assert.False(SledopytRelevance.IsFamiliar("rust новости", "https://unknown.io/x", visits, new()));
        // Знакомость и через обучение: 2 выбора хоста по запросу.
        var learning = new SledopytRelevance.LearningModel();
        learning.Record("рецепт борща", "povarenok.ru");
        learning.Record("рецепт борща", "povarenok.ru");
        Assert.True(SledopytRelevance.IsFamiliar("рецепт борща", "https://povarenok.ru/x", NoVisits, learning));
    }

    [Fact]
    public void Learning_StemGroupsSimilarQueries()
    {
        var learning = new SledopytRelevance.LearningModel();
        learning.Record("как приготовить борщ", "povarenok.ru");
        // «как приготовить» — общий стем: похожий запрос наследует вес.
        Assert.True(learning.Weight("как приготовить суп", "povarenok.ru") > 0);
        Assert.Equal(0, learning.Weight("купить машину", "povarenok.ru"));
    }

    [Fact]
    public void Learning_WeightIsCapped()
    {
        var learning = new SledopytRelevance.LearningModel();
        for (var i = 0; i < 20; i++)
            learning.Record("запрос", "example.com");
        Assert.True(learning.Weight("запрос", "example.com") <= 4.0);
    }

    [Fact]
    public void Terms_NormalizePunctuationAndCase()
    {
        var terms = SledopytRelevance.Terms("Rust, «async» — guide!");
        Assert.Contains("rust", terms);
        Assert.Contains("async", terms);
        Assert.Contains("guide", terms);
        Assert.DoesNotContain("—", terms);
    }
}
