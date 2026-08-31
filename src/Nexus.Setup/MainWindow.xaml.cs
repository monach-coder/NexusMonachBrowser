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

    private string _manifestUrl = DefaultManifestUrl;
    private string _publicKeyPem = string.Empty;
    private ReleaseManifestDto? _manifest;
    private string? _downloadedCoreZip;

    public static string DefaultInstallRoot
    {
        get
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "NexusMonach");
            // Умный дефолт: нейросетям нужно ~3 ГБ; если системный диск
            // забит, предлагаем самый свободный. Пользователь может сменить.
            try
            {
                var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData))!;
                var systemFree = new DriveInfo(systemDrive).AvailableFreeSpace;
                if (systemFree > 6L * 1024 * 1024 * 1024) return fallback;
                var roomiest = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .OrderByDescending(d => d.AvailableFreeSpace)
                    .FirstOrDefault();
                if (roomiest is { AvailableFreeSpace: > 12L * 1024 * 1024 * 1024 })
                    return Path.Combine(roomiest.RootDirectory.FullName, "NexusMonach");
            }
            catch { /* Любая ошибка — стандартный путь в профиле. */ }
            return fallback;
        }
    }

    private string _installRoot = DefaultInstallRoot;

    public MainWindow()
    {
        InitializeComponent();
        var manifestArg = Array.FindIndex(Environment.GetCommandLineArgs(),
            a => a.Equals("--manifest", StringComparison.OrdinalIgnoreCase));
        if (manifestArg >= 0 && Environment.GetCommandLineArgs().Length > manifestArg + 1)
            _manifestUrl = Environment.GetCommandLineArgs()[manifestArg + 1];
        var dirArg = Array.FindIndex(Environment.GetCommandLineArgs(),
            a => a.Equals("--install-dir", StringComparison.OrdinalIgnoreCase));
        if (dirArg >= 0 && Environment.GetCommandLineArgs().Length > dirArg + 1)
            _installRoot = Environment.GetCommandLineArgs()[dirArg + 1];
        if (Environment.GetCommandLineArgs().Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Loaded += (_, _) => UninstallAndClose();
        }
        else if (Environment.GetCommandLineArgs().Contains("--auto-install", StringComparer.OrdinalIgnoreCase))
        {
            Loaded += async (_, _) => await RunInstallAsync();
        }
        _publicKeyPem = LoadEmbeddedPublicKey();
        InstallDirBox.Text = _installRoot;
        UpdateDirHint();
        // Сцена кнопки: чистая установка, обновление или переустановка.
        // Версию из сети узнаем позже (манифест), тут — по установленной копии.
        var installedNow = GetInstalledVersion(_installRoot);
        if (installedNow is { } v)
            InstallButton.Content = $"Обновить (установлена {v})";
        Log("Установщик Nexus Monach готов.");
        InstallDirBox.TextChanged += (_, _) =>
        {
            _installRoot = InstallDirBox.Text.Trim();
            UpdateDirHint();
            var existing = GetInstalledVersion(_installRoot);
            InstallButton.Content = existing is { } ve
                ? $"Обновить (установлена {ve})"
                : "Установить";
        };
    }

    private string InstallRoot => _installRoot;

    private void UpdateDirHint()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(InstallDirBox.Text.Trim()))!;
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            DirHintText.Text = freeGb >= 4
                ? $"Диск {root} — свободно {freeGb:0.#} ГБ"
                : $"Диск {root} — всего {freeGb:0.#} ГБ свободно: нейросетям нужно ~3 ГБ, выберите другой диск";
        }
        catch
        {
            DirHintText.Text = string.Empty;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Папка установки Nexus Monach",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(InstallDirBox.Text) ? DefaultInstallRoot : InstallDirBox.Text
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            InstallDirBox.Text = Path.Combine(dialog.SelectedPath, "NexusMonach");
            UpdateDirHint();
        }
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

    /// <summary>Закрыть установщик (крестик или «Готово»).</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Кнопка «Готово» после успешной установки/удаления.</summary>
    private void Finish_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Отмена установки: останавливает загрузку и возвращает кнопки.</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _installCts?.Cancel();
        Status("Установка отменена пользователем.");
        CancelButton.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = true;
        DirPanel.Visibility = Visibility.Visible;
    }

    private CancellationTokenSource? _installCts;

    private async Task RunInstallAsync()
    {
        InstallButton.IsEnabled = false;
        InstallButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        DirPanel.Visibility = Visibility.Collapsed;
        _installCts = new CancellationTokenSource();
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

            // 1b. Проверка обновлений на стадии установки: манифест всегда
            // указывает на latest-релиз, поэтому даже старый установщик
            // ставит самую свежую версию — и честно говорит об этом.
            var available = ParseVersion(_manifest.Version);
            var installed = GetInstalledVersion(InstallRoot);
            if (installed is { } have)
            {
                Status(available > have
                    ? $"Обновление: {have} → {available}. Скачиваю ядро…"
                    : $"Установлена актуальная версия {have}. Будет переустановлена {available}.");
                Log(installed is null ? "" : $"Найдена установка {have}, доступна {available}.");
            }
            else
            {
                Status($"Проверка обновлений: актуальная версия {available}. Скачиваю ядро…");
            }

            // 2. Ядро браузера.
            var core = _manifest.Files.FirstOrDefault(f => f.Group == "core") ??
                       throw new InvalidOperationException("В манифесте нет ядра браузера.");
            Status("Загрузка ядра браузера…");
            _downloadedCoreZip = await DownloadVerifiedAsync(http, core,
                Path.Combine(Path.GetTempPath(), core.RelativePath));

            // 3. Распаковка per-user, без прав администратора. Профиль Data
            // пользователя неприкосновенен: выносим, обновляем, возвращаем.
            Status("Распаковка…");
            var dataPreserved = PreserveData();
            try
            {
                if (Directory.Exists(InstallRoot))
                    Directory.Delete(InstallRoot, recursive: true);
                ZipFile.ExtractToDirectory(_downloadedCoreZip, InstallRoot);
            }
            finally
            {
                RestoreData(dataPreserved);
            }
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

            // Установка завершена: показываем «Готово» — пользователь сам
            // закрывает, когда готов (или сразу запускает браузер).
            Status($"Nexus Monach {_manifest.Version} установлен!");
            CancelButton.Visibility = Visibility.Collapsed;
            SkipButton.Visibility = Visibility.Collapsed;
            FinishButton.Visibility = Visibility.Visible;
            PhaseText.Text = "Нажмите «Готово» для выхода. Браузер можно запустить с ярлыка на рабочем столе.";

            // 5. Нейросети — здесь же, в установщике: перевод, голос и
            // распознавание готовы к первому запуску. Медленный канал —
            // «Пропустить», и браузер докачает фоном сам.
            var aiPacks = _manifest.Files.Where(f => f.Group == "ai").ToList();
            if (aiPacks.Count > 0)
            {
                _aiPhase = true;
                SkipButton.Visibility = Visibility.Visible;
                InstallButton.Visibility = Visibility.Collapsed;
                var done = 0;
                foreach (var pack in aiPacks)
                {
                    if (_skipAi) break;
                    Status("Нейросети: " + pack.Purpose + "…");
                    var stagedZip = Path.Combine(Path.GetTempPath(), pack.RelativePath);
                    await DownloadVerifiedAsync(http, pack, stagedZip);
                    Status("Распаковка: " + pack.Purpose + "…");
                    ZipFile.ExtractToDirectory(stagedZip, InstallRoot, overwriteFiles: true);
                    try { File.Delete(stagedZip); } catch { }
                    done++;
                }
                SkipButton.Visibility = Visibility.Collapsed;
                _aiPhase = false;
                Status(done == aiPacks.Count
                    ? "Готово! Нейросети установлены — запуск браузера…"
                    : "Ядро установлено. Нейросети докачает браузер в фоне.");
            }
            else
            {
                Status("Готово! Браузер запускается…");
            }
            Dispatcher.Invoke(() => ProgressFill.Width = ActualWidth > 0 ? ActualWidth - 80 : 480);

            // Одна кнопка — один поток: запуск и тихий выход. Порт-щит
            // сработает беззвучно: уведомительный режим по умолчанию.
            try
            {
                var guardian = Path.Combine(InstallRoot, "NexusMonach.exe");
                if (File.Exists(guardian))
                    using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guardian)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = InstallRoot
                    })) { }
            }
            catch { /* Браузер запустят ярлыком. */ }
            await Task.Delay(1500);
            Close();
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

    private bool _aiPhase;
    private volatile bool _skipAi;

    /// <summary>Версия установленной копии в выбранной папке, если она есть.</summary>
    internal static Version? GetInstalledVersion(string installRoot)
    {
        try
        {
            var browser = Path.Combine(installRoot, "NexusMonach.Browser.exe");
            if (!File.Exists(browser)) return null;
            var text = System.Diagnostics.FileVersionInfo.GetVersionInfo(browser).ProductVersion;
            return text is null ? null : ParseVersion(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>«2.9.8+abc…» → 2.9.8; мусор → 0.0.0.</summary>
    internal static Version ParseVersion(string value)
    {
        var digits = new string(value.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0, 0);
    }

    /// <summary>Выносим профиль пользователя перед обновлением ядра.</summary>
    private string? PreserveData()
    {
        var data = Path.Combine(InstallRoot, "Data");
        if (!Directory.Exists(data)) return null;
        var backup = Path.Combine(Path.GetTempPath(),
            "nexus-data-" + Guid.NewGuid().ToString("N"));
        Directory.Move(data, backup);
        return backup;
    }

    private void RestoreData(string? backup)
    {
        if (backup is null || !Directory.Exists(backup)) return;
        var destination = Path.Combine(InstallRoot, "Data");
        try
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(backup, destination);
        }
        catch
        {
            // Профиль дороже чистоты: оставляем бэкап и кричим в лог.
            Log("Не удалось вернуть профиль Data из " + backup);
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _skipAi = true;
        SkipButton.IsEnabled = false;
        SkipButton.Content = "Пропускаем…";
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private async Task<string> DownloadVerifiedAsync(HttpClient http, ManifestFileDto file,
        string destinationPath, bool isRetry = false)
    {
        var baseUrl = new Uri(new Uri(_manifestUrl), ".");
        var url = new Uri(baseUrl, file.RelativePath);
        var partPath = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        // Докачка валидна только для того же релиза: чужой .part склеит
        // байты двух версий. При повторе стартуем с нуля.
        long offset = !isRetry && File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (isRetry && File.Exists(partPath)) File.Delete(partPath);

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
                // Кастомная полоса: ширина заливки в пикселях по факту окна.
                if (total > 0 && ActualWidth > 0)
                    ProgressFill.Width = (ActualWidth - 80) * Math.Clamp(
                        received * 1.0 / total, 0, 1);
                DetailText.Text = $"{file.Purpose}: {received / 1024 / 1024} / {total / 1024 / 1024} МБ";
            });
        }
        local.Close();

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(partPath))).ToLowerInvariant();
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            // Склеенный чужой .part или обрыв: одна чистая попытка с нуля.
            if (!isRetry)
            {
                Log($"Хеш не сошёлся ({file.RelativePath}), повтор с нуля…");
                return await DownloadVerifiedAsync(http, file, destinationPath, isRetry: true);
            }
            throw new InvalidOperationException(
                $"Хеш не совпал: {file.RelativePath}. Файл удалён, повторите установку.");
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
            // Показываем «Готово» — окно закрывается по кнопке, не висит.
            CancelButton.Visibility = Visibility.Collapsed;
            FinishButton.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Collapsed;
            PhaseText.Text = "Nexus Monach полностью удалён. Нажмите «Готово» для выхода.";
        }
        catch (Exception ex)
        {
            Status("Ошибка удаления: " + ex.Message);
            CancelButton.Visibility = Visibility.Collapsed;
            FinishButton.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Collapsed;
            PhaseText.Text = "Ошибка удаления. Нажмите «Готово» для выхода.";
        }
    }

    private void Status(string text) => Dispatcher.Invoke(() =>
    {
        StatusText.Text = text;
        PhaseText.Text = text;
    });
    private void Log(string line) => Dispatcher.Invoke(() =>
    {
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
