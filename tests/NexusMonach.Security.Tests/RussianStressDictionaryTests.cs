using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class RussianStressDictionaryTests
{
    [Fact]
    public void NormativeStress_IsPlacedBeforeStressedVowel()
    {
        EnsureDictionaryLoaded();
        Assert.Equal("догов+ор", RussianStressDictionary.TryStressWord("договор"));
        Assert.Equal("звон+ит", RussianStressDictionary.TryStressWord("звонит"));
        Assert.Equal("щав+ель", RussianStressDictionary.TryStressWord("щавель"));
        Assert.Equal("катал+ог", RussianStressDictionary.TryStressWord("каталог"));
        Assert.Equal("крепостн+ой", RussianStressDictionary.TryStressWord("крепостной"));
    }

    [Fact]
    public void OriginalCaseIsPreserved()
    {
        EnsureDictionaryLoaded();
        Assert.Equal("Догов+ор", RussianStressDictionary.TryStressWord("Договор"));
        Assert.Equal("ДОГОВ+ОР", RussianStressDictionary.TryStressWord("ДОГОВОР"));
    }

    [Fact]
    public void UnknownYoAndSingleVowelWords_StayUntouched()
    {
        EnsureDictionaryLoaded();
        Assert.Null(RussianStressDictionary.TryStressWord("несуществующееслово"));
        Assert.Null(RussianStressDictionary.TryStressWord("ёлка"));
        Assert.Null(RussianStressDictionary.TryStressWord("мир"));
    }

    [Fact]
    public void ApplyStress_MarksOnlyKnownCyrillicWords()
    {
        EnsureDictionaryLoaded();
        var marked = RussianStressDictionary.ApplyStress("Договор подписан, каталог обновлён, цена 100");
        Assert.Contains("Догов+ор", marked);
        Assert.Contains("катал+ог", marked);
        // «обновлён» содержит ё: ударение уже видно из буквы, маркер не нужен.
        Assert.DoesNotContain("+ён", marked);
        Assert.DoesNotContain("+ ", marked);
    }

    [Fact]
    public void EveryDictionaryWord_RoundTripsThroughVowelIndex()
    {
        EnsureDictionaryLoaded();
        // Индекс из словаря обязан указывать на реальную гласную исходного слова:
        // маркер всегда встаёт внутрь слова и ровно один.
        var marked = RussianStressDictionary.ApplyStress("перезвонит включит средства нарочно");
        Assert.Equal("перезвон+ит включ+ит ср+едства нар+очно", marked);
    }

    [Fact]
    public void UpstreamDictionaryErrors_AreCorrected()
    {
        EnsureDictionaryLoaded();
        // В исходном словаре RUAccent «готов» ошибочно помечено как «г+отов»,
        // хотя вся семья (готова, готово, готовить) ударяется на вторую «о»;
        // родительный падеж «утра» — утрА, как в «десять утра».
        Assert.Equal("гот+ов", RussianStressDictionary.TryStressWord("готов"));
        Assert.Equal("утр+а", RussianStressDictionary.TryStressWord("утра"));
        Assert.Equal("Гот+ов", RussianStressDictionary.TryStressWord("Готов"));
    }

    private static void EnsureDictionaryLoaded()
    {
        if (RussianStressDictionary.IsReady) return;
        var candidates = new[]
        {
            AiModelCatalog.StressDictionary,
            Path.Combine(FindRepositoryRoot(), "src", "NexusMonach", "AI",
                "dictionaries", "ru-stress-full.txt.gz")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        Assert.False(path is null, "Словарь ударений ru-stress-full.txt.gz не найден ни в выводе сборки, ни в исходном дереве.");
        RussianStressDictionary.LoadFrom(path!);
        Assert.True(RussianStressDictionary.IsReady);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NexusMonach.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
