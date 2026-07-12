using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexusMonach.Models;

namespace NexusMonach.Services;

public enum DownloadRiskLevel
{
    Low,
    Medium,
    High
}

public sealed record DownloadRiskAssessment(DownloadRiskLevel Level, string Description);

public static class DownloadSecurityService
{
    private static readonly HashSet<string> HighRiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".reg", ".lnk", ".pif", ".cpl", ".dll", ".sys", ".jar", ".chm", ".iso", ".img"
    };

    private static readonly HashSet<string> MediumRiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".docm", ".xlsm", ".pptm", ".xll"
    };

    private static readonly HashSet<string> DecoyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".jpg", ".jpeg", ".png", ".txt", ".rtf"
    };

    public static DownloadRiskAssessment Assess(string fileName, string sourceUrl)
    {
        var extension = Path.GetExtension(fileName);
        var nameWithoutFinalExtension = Path.GetFileNameWithoutExtension(fileName);
        var previousExtension = Path.GetExtension(nameWithoutFinalExtension);

        if (HighRiskExtensions.Contains(extension) && DecoyExtensions.Contains(previousExtension))
            return new DownloadRiskAssessment(DownloadRiskLevel.High,
                $"Двойное расширение {previousExtension}{extension} может скрывать исполняемый файл");

        if (HighRiskExtensions.Contains(extension))
            return new DownloadRiskAssessment(DownloadRiskLevel.High,
                $"Исполняемый или системный тип файла {extension}");

        if (MediumRiskExtensions.Contains(extension))
            return new DownloadRiskAssessment(DownloadRiskLevel.Medium,
                $"Архив или документ с активным содержимым {extension}");

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
            return new DownloadRiskAssessment(DownloadRiskLevel.Medium,
                "Загрузка получена без подтверждённого HTTPS-соединения");

        return new DownloadRiskAssessment(DownloadRiskLevel.Low, "Явных локальных признаков риска не найдено");
    }

    public static async Task InspectCompletedAsync(DownloadItem item)
    {
        var preliminary = Assess(item.FileName, item.SourceUrl);
        SetAssessment(item, preliminary);

        if (!File.Exists(item.FilePath))
        {
            item.SecurityDetails = "Файл не найден после загрузки";
            return;
        }

        try
        {
            await using var stream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream);
            item.Sha256 = Convert.ToHexString(hash);
        }
        catch (Exception ex)
        {
            item.Sha256 = "Ошибка вычисления: " + ex.Message;
        }

        item.SignatureInfo = ReadSignature(item.FilePath);
    }

    public static void SetAssessment(DownloadItem item, DownloadRiskAssessment assessment)
    {
        item.RiskLevel = assessment.Level switch
        {
            DownloadRiskLevel.High => "высокий",
            DownloadRiskLevel.Medium => "средний",
            _ => "низкий"
        };
        item.SecurityDetails = assessment.Description;
    }

    private static string ReadSignature(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return string.IsNullOrWhiteSpace(certificate.Subject)
                ? "В файле найден сертификат подписи"
                : "Сертификат в файле: " + certificate.Subject;
        }
        catch
        {
            return "Сертификат цифровой подписи не обнаружен";
        }
    }
}
