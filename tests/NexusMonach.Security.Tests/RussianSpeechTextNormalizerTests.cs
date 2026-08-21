using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class RussianSpeechTextNormalizerTests
{
    [Theory]
    [InlineData("Nexus Monach использует WebView2", "Нексус Монах использует веб-вью два")]
    [InlineData("Встреча 02.08.2026 в 09:30", "Встреча второго августа две тысячи двадцать шестого года в девять часов тридцать минут")]
    [InlineData("Загрузка 2,5 ГБ завершена на 100%", "Загрузка две целых пять десятых гигабайта завершена на сто процентов")]
    [InlineData("Температура -21 °C", "Температура минус двадцать один градус Цельсия")]
    [InlineData("Счёт №7: 1250 рублей", "Счёт номер семь: одна тысяча двести пятьдесят рублей")]
    [InlineData("Цена $12 и 5 евро", "Цена двенадцать долларов и пять евро")]
    [InlineData("Дата 31.12.1999", "Дата тридцать первого декабря тысяча девятьсот девяносто девятого года")]
    public void WrittenRussian_IsConvertedToNaturalSpeech(string input, string expected)
    {
        Assert.Equal(expected, RussianSpeechTextNormalizer.Normalize(input));
    }

    [Fact]
    public void LargeNumbers_UseCorrectScaleGenderAndForms()
    {
        Assert.Equal("один миллион две тысячи триста сорок пять",
            RussianSpeechTextNormalizer.Normalize("1002345"));
    }

    [Fact]
    public void Punctuation_IsCollapsedWithoutRemovingIntonation()
    {
        Assert.Equal("Готово! Что дальше?", RussianSpeechTextNormalizer.Normalize("Готово!!! Что дальше???"));
    }

    [Fact]
    public void ImpossibleCalendarDate_IsNotInvented()
    {
        Assert.Equal("Дата 31.02.2026", RussianSpeechTextNormalizer.Normalize("Дата 31.02.2026"));
    }

    [Theory]
    [InlineData("Chrome", "кроум")]
    [InlineData("iPhone", "айфон")]
    [InlineData("WiFi", "вай-фай")]
    [InlineData("shutdown", "шутдаун")]
    [InlineData("John Connor", "джон конор")]
    [InlineData("check", "чек")]
    public void EnglishWords_ArePronouncedWithRussianPhonetics(string input, string expected)
    {
        Assert.Equal(expected, RussianSpeechTextNormalizer.Normalize(input));
    }

    [Fact]
    public void Acronyms_AreSpelledByLetterNames()
    {
        Assert.Equal("эйч-би-оу", RussianSpeechTextNormalizer.Normalize("HBO"));
    }

    [Fact]
    public void EnglishInsideRussianSpeech_BecomesReadable()
    {
        var spoken = RussianSpeechTextNormalizer.Normalize(
            "Смотрите новый trailer на YouTube channel.");
        Assert.Contains("трейлэр", spoken);
        Assert.Contains("ютуб", spoken);
        Assert.DoesNotContain("trailer", spoken, StringComparison.OrdinalIgnoreCase);
    }
}
