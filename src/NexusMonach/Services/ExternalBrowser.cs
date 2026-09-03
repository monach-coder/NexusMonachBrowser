using System.Runtime.InteropServices;

namespace NexusMonach.Services;

/// <summary>
/// Открытие веб-страницы в браузере по умолчанию. Схема жёстко ограничена
/// https: через этот путь можно передать системе только веб-ссылку.
/// </summary>
internal static class ExternalBrowser
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string? operation, string file,
        string? parameters, string? directory, int showCommand);

    private const int SwShowNormal = 1;

    public static void OpenHttps(Uri page)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps) return;
        // «open» + https-URL: ссылка уходит обработчику протокола (браузер
        // по умолчанию); исполнение команд через этот вызов недостижимо.
        ShellExecuteW(IntPtr.Zero, "open", page.AbsoluteUri, null, null, SwShowNormal);
    }
}
