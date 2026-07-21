using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using NexusMonach.Models;

namespace NexusMonach.Services;

public enum DownloadRiskLevel
{
    Low,
    Medium,
    High
}

public sealed record DownloadRiskAssessment(DownloadRiskLevel Level, string Description);
public sealed record AuthenticodeAssessment(bool IsSigned, bool IsTrusted, string Description);

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

    public static string SanitizeSourceForDisplay(string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) ||
            source.Scheme is not "http" and not "https" ||
            string.IsNullOrWhiteSpace(source.Host) ||
            !string.IsNullOrEmpty(source.UserInfo))
            return "неизвестный источник";

        return new UriBuilder(source.Scheme, source.IdnHost, source.IsDefaultPort ? -1 : source.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
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

        var signature = VerifyAuthenticode(item.FilePath);
        item.SignatureTrusted = signature.IsTrusted;
        item.SignatureInfo = signature.Description;
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
        item.RequiresOpenConfirmation = assessment.Level is DownloadRiskLevel.High or DownloadRiskLevel.Medium;
    }

    public static AuthenticodeAssessment VerifyAuthenticode(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
            return new AuthenticodeAssessment(false, false, "Authenticode: файл недоступен для проверки");

        IntPtr filePathPointer = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        IntPtr trustDataPointer = IntPtr.Zero;
        var action = WinTrustActionGenericVerifyV2;
        var trustData = new WinTrustData();
        try
        {
            filePathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
            var fileInfo = new WinTrustFileInfo
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            trustData = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // no network revocation lookup
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfoPointer,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x00001000 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);

            var status = WinVerifyTrust(IntPtr.Zero, action, trustDataPointer);
            if (status == 0)
            {
                string signer;
                try
                {
                    using var certificate = X509Certificate.CreateFromSignedFile(path);
                    signer = certificate.Subject;
                }
                catch
                {
                    signer = string.Empty;
                }

                return new AuthenticodeAssessment(true, true,
                    string.IsNullOrWhiteSpace(signer)
                        ? "Authenticode: подпись доверена Windows"
                        : "Authenticode: доверенная подпись · " + signer);
            }

            var unsigned = status == unchecked((int)0x800B0100) ||
                           status == unchecked((int)0x800B0003) ||
                           status == unchecked((int)0x800B0004);
            return unsigned
                ? new AuthenticodeAssessment(false, false, "Authenticode: подпись отсутствует")
                : new AuthenticodeAssessment(true, false,
                    $"Authenticode: подпись недействительна или не доверена Windows (0x{status:X8})");
        }
        catch (Exception ex)
        {
            return new AuthenticodeAssessment(false, false,
                "Authenticode: ошибка проверки · " + ex.GetType().Name);
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                try
                {
                    trustData = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
                    trustData.StateAction = 2; // WTD_STATEACTION_CLOSE
                    Marshal.StructureToPtr(trustData, trustDataPointer, false);
                    _ = WinVerifyTrust(IntPtr.Zero, action, trustDataPointer);
                }
                catch { }
                Marshal.FreeHGlobal(trustDataPointer);
            }
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPointer);
            if (filePathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint CbStruct;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr trustData);
}
