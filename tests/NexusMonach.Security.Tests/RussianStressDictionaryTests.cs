using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class RussianStressDictionaryTests
{
    [Fact]
    public void ApplyStress_MarksWithPlusBetweenCyrillic()
    {
        EnsureDictionaryLoaded();
        var spoken = RussianStressDictionary.ApplyStress(
            "Договор подписан, каталог обновлён, ещё вечером привезёт её чемодан.");
        // Маркер «+» между кириллическими буквами — документированное
        // ударение Silero; воркер защищает его от замены на « плюс ».
        Assert.Contains("Догов+ор", spoken);
        Assert.Contains("катал+ог", spoken);
    }

    [Fact]
    public void NormativeStress_IsPlacedBeforeStressedVowel()
    {
        EnsureDictionaryLoaded();
        Assert.Equal("догов+ор", RussianStressDictionary.TryStressWord("договор"));
        Assert.Equal("звон+ит", RussianStressDictionary.TryStressWord("звонит"));
        Assert.Equal("катал+ог", RussianStressDictionary.TryStressWord("каталог"));
    }

    [Fact]
    public void UpstreamDictionaryErrors_AreCorrected()
    {
        EnsureDictionaryLoaded();
        // «готов» в исходном словаре RUAccent ошибочно ударяется на первую
        // «о»; семья слова (готова, готово, готовить) ударяется на вторую.
        Assert.Equal("гот+ов", RussianStressDictionary.TryStressWord("готов"));
        Assert.Equal("утр+а", RussianStressDictionary.TryStressWord("утра"));
    }

    [Fact]
    public void MachineTranslationWithoutYo_GetsYoRestored()
    {
        EnsureDictionaryLoaded();
        var spoken = RussianStressDictionary.ApplyStress(
            "Он приедет еще вечером и привезет ее чемодан.");
        Assert.Contains("ещё", spoken);
        Assert.Contains("её", spoken);
        Assert.Contains("привезёт", spoken);
    }

    private static void EnsureDictionaryLoaded()
    {
        if (RussianStressDictionary.IsReady) return;
        var root = FindRepositoryRoot();
        var candidates = new[]
        {
            AiModelCatalog.StressDictionary,
            Path.Combine(root, "src", "NexusMonach", "AI",
                "dictionaries", "ru-stress-full.txt.gz")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        Assert.False(path is null, "Словарь ru-stress-full.txt.gz не найден ни в выводе сборки, ни в исходном дереве.");
        var yoPath = Path.Combine(root, "src", "NexusMonach", "AI",
            "dictionaries", "ru-yo-words.txt.gz");
        RussianStressDictionary.LoadFrom(path!,
            File.Exists(yoPath) ? yoPath : null);
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
