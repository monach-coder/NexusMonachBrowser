using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Nexus.Setup;

/// <summary>
/// Лёгкий установщик: скачивает подписанный манифест поставки, загружает
/// ядро браузера с проверкой SHA-256 и подписи, распаковывает в профиль
/// пользователя (%LOCALAPPDATA%\Programs\NexusMonach), регистрирует
/// протокол nexus://, запись «Установка и удаление программ» и ярлыки.
/// AI-модели подтягиваются по желанию — браузер работает и без них.
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultManifestUrl =
        "https://github.com/monach-coder/NexusMonachBrowser/releases/latest/download/release-manifest.json";

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "NexusMonach");

    private string _manifestUrl = DefaultManifestUrl;
    private string _publicKeyPem = string.Empty;
    private ReleaseManifestDto? _manifest;
    private string? _downloadedCoreZip;

    public MainWindow()
    {
        InitializeComponent();
        var manifestArg = Array.FindIndex(Environment.GetCommandLineArgs(),
            a => a.Equals("--manifest", StringComparison.OrdinalIgnoreCase));
        if (manifestArg >= 0 && Environment.GetCommandLineArgs().Length > manifestArg + 1)
            _manifestUrl = Environment.GetCommandLineArgs()[manifestArg + 1];
        if (Environment.GetCommandLineArgs().Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Loaded += (_, _) => UninstallAndClose();
        }
        else if (Environment.GetCommandLineArgs().Contains("--auto-install", StringComparer.OrdinalIgnoreCase))
        {
            Loaded += async (_, _) => await RunInstallAsync();
        }
        _publicKeyPem = LoadEmbeddedPublicKey();
        Log("Установщик Nexus Monach готов.");
    }

    private static string LoadEmbeddedPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Nexus.Setup.integrity-public-key.pem");
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await RunInstallAsync();

    private async Task RunInstallAsync()
    {
        InstallButton.IsEnabled = false;
        try
        {
            // 1. Манифест и подпись: компрометация зеркала не пройдёт проверку.
            Status("Проверка манифеста поставки…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var manifestBytes = await http.GetByteArrayAsync(_manifestUrl);
            var signature = await http.GetStringAsync(_manifestUrl.TrimEnd('/') + ".sig");
            if (string.IsNullOrWhiteSpace(_publicKeyPem))
                throw new InvalidOperationException("В сборке установщика нет публичного ключа Guardian.");
            if (!VerifySignature(manifestBytes, signature.Trim(), _publicKeyPem))
                throw new InvalidOperationException("Подпись манифеста недействительна — источник скомпрометирован.");
            _manifest = JsonSerializer.Deserialize<ReleaseManifestDto>(manifestBytes) ??
                        throw new InvalidOperationException("Манифест повреждён.");
            Log($"Манифест принят: версия {_manifest.Version}, файлов {_manifest.Files.Count}.");

            // 2. Ядро браузера.
            var core = _manifest.Files.FirstOrDefault(f => f.Group == "core") ??
                       throw new InvalidOperationException("В манифесте нет ядра браузера.");
            Status("Загрузка ядра браузера…");
            _downloadedCoreZip = await DownloadVerifiedAsync(http, core,
                Path.Combine(Path.GetTempPath(), core.RelativePath));

            // 3. Распаковка per-user, без прав администратора.
            Status("Распаковка…");
            if (Directory.Exists(InstallRoot))
                Directory.Delete(InstallRoot, recursive: true);
            ZipFile.ExtractToDirectory(_downloadedCoreZip, InstallRoot);
            Log("Установлено в " + InstallRoot);

            // Предпосев настроек: установленный браузер знает, откуда докачивать
            // нейросети, и подтянет их в фоне без вопросов пользователю.
            var dataDirectory = Path.Combine(InstallRoot, "Data");
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"),
                JsonSerializer.Serialize(new { AiPackManifestUrl = _manifestUrl }));

            // 4. Регистрация: протокол, «Установка и удаление», ярлыки.
            Status("Регистрация в системе…");
            RegisterProtocol();
            RegisterUninstallEntry(_manifest.Version);
            CreateShortcuts();
            Log("Протокол nexus://, запись удаления и ярлыки созданы.");

            Status("Готово! Nexus Monach установлен.");
            Progress.Value = 100;
            LaunchButton.Visibility = Visibility.Visible;
            AiButton.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Status("Ошибка установки: " + ex.Message);
            Log("ОШИБКА: " + ex);
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "nexus-setup.log"),
                    "ПОЛНЫЙ СТЕК: " + ex + Environment.NewLine);
            }
            catch { }
            InstallButton.IsEnabled = true;
        }
    }

    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        AiButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var aiFiles = (_manifest?.Files ?? []).Where(f => f.Group == "ai").ToList();
            foreach (var file in aiFiles)
            {
                Status("AI-модели: " + file.Purpose + "…");
                var stagedZip = Path.Combine(Path.GetTempPath(), file.RelativePath);
                await DownloadVerifiedAsync(http, file, stagedZip);
                // Архив содержит дерево AI/ — распаковываем в корень установки.
                ZipFile.ExtractToDirectory(stagedZip, InstallRoot, overwriteFiles: true);
                try { File.Delete(stagedZip); } catch { }
                Log("Распаковано в " + InstallRoot);
            }
            Status("AI-модели загружены — перевод, голос и поиск доступны.");
        }
        catch (Exception ex)
        {
            Status("AI-модели: " + ex.Message + " (браузер продолжает работать)");
            Log("AI: " + ex);
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var guardian = Path.Combine(InstallRoot, "NexusMonach.exe");
        if (File.Exists(guardian))
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guardian)
            {
                UseShellExecute = true,
                WorkingDirectory = InstallRoot
            });
        }
        Close();
    }

    private async Task<string> DownloadVerifiedAsync(HttpClient http, ManifestFileDto file, string destinationPath)
    {
        var baseUrl = new Uri(new Uri(_manifestUrl), ".");
        var url = new Uri(baseUrl, file.RelativePath);
        var partPath = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = offset + (response.Content.Headers.ContentLength ?? 0);
        await using var remote = await response.Content.ReadAsStreamAsync();
        await using var local = new FileStream(partPath,
            offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 16];
        long received = offset;
        int read;
        while ((read = await remote.ReadAsync(buffer)) > 0)
        {
            await local.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            Dispatcher.Invoke(() =>
            {
                if (total > 0) Progress.Value = received * 100.0 / total;
                DetailText.Text = $"{file.Purpose}: {received / 1024 / 1024} / {total / 1024 / 1024} МБ";
            });
        }
        local.Close();

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(partPath))).ToLowerInvariant();
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new InvalidOperationException($"Хеш не совпал: {file.RelativePath}. Файл удалён, повторите установку.");
        }
        File.Move(partPath, destinationPath, overwrite: true);
        Log($"✓ {file.RelativePath}");
        return destinationPath;
    }

    private static bool VerifySignature(byte[] manifestBytes, string signatureBase64, string publicKeyPem)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private void RegisterProtocol()
    {
        using var classes = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\nexus");
        classes.SetValue(string.Empty, "URL:протокол Nexus Monach");
        classes.SetValue("URL Protocol", string.Empty);
        using var command = classes.CreateSubKey(@"shell\open\command");
        command.SetValue(string.Empty, $"\"{Path.Combine(InstallRoot, "NexusMonach.exe")}\" \"%1\"");
    }

    private void RegisterUninstallEntry(string version)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusMonach");
        key.SetValue("DisplayName", "Nexus Monach Browser");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "monach-coder");
        key.SetValue("InstallLocation", InstallRoot);
        key.SetValue("DisplayIcon", Path.Combine(InstallRoot, "NexusMonach.exe,0"));
        var setupCopy = Path.Combine(InstallRoot, "NexusMonach-Setup.exe");
        key.SetValue("UninstallString", $"\"{setupCopy}\" --uninstall");
    }

    private void CreateShortcuts()
    {
        // Копия установщика внутри установки становится деинсталлятором.
        var setupExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(setupExe))
            File.Copy(setupExe, Path.Combine(InstallRoot, "NexusMonach-Setup.exe"), overwrite: true);
        CreateShortcut("Nexus Monach",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(InstallRoot, "NexusMonach.exe"));
        CreateShortcut("Nexus Monach",
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(InstallRoot, "NexusMonach.exe"));
    }

    private static void CreateShortcut(string name, string directory, string target)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(Path.Combine(directory, name + ".lnk"));
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = Path.GetDirectoryName(target);
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
        }
        catch
        {
            // Ярлык — удобство, а не условие установки.
        }
    }

    private void UninstallAndClose()
    {
        Status("Удаление Nexus Monach…");
        try
        {
            foreach (var shortcutDir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                         Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                     })
                File.Delete(Path.Combine(shortcutDir, "Nexus Monach.lnk"));
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\nexus", throwOnMissingSubKey: false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusMonach", throwOnMissingSubKey: false);
            // Каталог удаляем отложенно: браузер может быть запущен.
            var cleanupBat = Path.Combine(Path.GetTempPath(), "nexus-uninstall-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(cleanupBat,
                "@echo off\r\ntimeout /t 3 /nobreak >nul\r\nrd /s /q \"" + InstallRoot + "\"\r\ndel \"%~f0\"\r\n");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cleanupBat)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Status("Nexus Monach удалён.");
            Log("Файлы будут стёрты через несколько секунд.");
        }
        catch (Exception ex)
        {
            Status("Ошибка удаления: " + ex.Message);
        }
    }

    private void Status(string text) => Dispatcher.Invoke(() => StatusText.Text = text);
    private void Log(string line) => Dispatcher.Invoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "nexus-setup.log"),
                DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch { }
    });

    internal sealed class ReleaseManifestDto
    {
        public int SchemaVersion { get; set; }
        public string Product { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public List<ManifestFileDto> Files { get; set; } = [];
    }

    internal sealed class ManifestFileDto
    {
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Group { get; set; } = "ai";
        public string Purpose { get; set; } = string.Empty;
    }
}
