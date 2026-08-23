using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class VoiceCommandRouterTests
{
    [Theory]
    [InlineData("переведи страницу", (int)VoiceCommandKind.TranslatePage, "")]
    [InlineData("переведи видео", (int)VoiceCommandKind.TranslateVideo, "")]
    [InlineData("открой гардиан", (int)VoiceCommandKind.OpenGuardian, "")]
    [InlineData("новая вкладка", (int)VoiceCommandKind.NewTab, "")]
    [InlineData("найди локальный приватный браузер", (int)VoiceCommandKind.Search, "локальный приватный браузер")]
    [InlineData("найди в сети новости технологий", (int)VoiceCommandKind.Search, "новости технологий")]
    public void PushToTalk_ParsesSupportedCommands(string transcript, int kindValue, string argument)
    {
        var command = VoiceCommandRouter.Parse(transcript, requireWakeWord: false);

        Assert.Equal((VoiceCommandKind)kindValue, command.Kind);
        Assert.Equal(argument, command.Argument);
    }

    [Fact]
    public void HandsFree_RequiresWakeWord()
    {
        Assert.Equal(VoiceCommandKind.None,
            VoiceCommandRouter.Parse("переведи страницу", requireWakeWord: true).Kind);
        Assert.Equal(VoiceCommandKind.TranslatePage,
            VoiceCommandRouter.Parse("Нексус, переведи страницу", requireWakeWord: true).Kind);
    }

    [Fact]
    public void HandsFree_AcceptsGarbledWakeWord()
    {
        // Живой whisper слышит слово-пароль по-разному — точное сравнение
        // роняло почти каждую попытку, браузер «слушал и молчал».
        Assert.Equal(VoiceCommandKind.OpenSettings,
            VoiceCommandRouter.Parse("нэксус открой настройки", requireWakeWord: true).Kind);
        Assert.Equal(VoiceCommandKind.OpenSettings,
            VoiceCommandRouter.Parse("нексис, настройки", requireWakeWord: true).Kind);
        Assert.Equal(VoiceCommandKind.NewTab,
            VoiceCommandRouter.Parse("некст новая вкладка", requireWakeWord: true).Kind);
    }

    [Fact]
    public void HandsFree_IgnoresOrdinarySpeech()
    {
        // Нечёткое совпадение не должно превращать любую похожесть в пароль.
        Assert.Equal(VoiceCommandKind.None,
            VoiceCommandRouter.Parse("открой настройки пожалуйста", requireWakeWord: true).Kind);
        Assert.Equal(VoiceCommandKind.None,
            VoiceCommandRouter.Parse("просто текст про экспорт", requireWakeWord: true).Kind);
    }

    [Fact]
    public void UnknownSpeech_DoesNotBecomeSearchAutomatically()
    {
        Assert.Equal(VoiceCommandKind.None,
            VoiceCommandRouter.Parse("мой пароль сегодня прекрасный", requireWakeWord: false).Kind);
    }
}
