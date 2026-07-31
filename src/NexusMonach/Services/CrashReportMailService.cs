using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Services;

public sealed record CrashMailComposeResult(
    bool Opened,
    bool AttachmentAdded,
    string Message,
    string AttachmentPath);

public static class CrashReportMailService
{
    public const string Recipient = "nexus.guardian.reports@proton.me";

    private const string PublicKeyResource = "NexusMonach.crash-report-public-key.pem";
    private const string Algorithm = "RSA-OAEP-SHA256+A256GCM";
    private const int MapiTo = 1;
    private const int MapiLogonUi = 0x00000001;
    private const int MapiNewSession = 0x00000002;
    private const int MapiDialog = 0x00000008;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("NexusGuardianCrashReport/v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsEncryptionReady =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(PublicKeyResource) is not null;

    public static CrashMailComposeResult Compose(GuardianReportSnapshot report, nint ownerHandle)
    {
        ArgumentNullException.ThrowIfNull(report);
        var attachment = CreateEncryptedAttachment(report);
        var severity = report.Fatal ? "CRITICAL" : "ERROR";
        var component = SafeSubjectToken(report.Component, "unknown");
        var version = SafeSubjectToken(report.BrowserVersion, "unknown");
        var fingerprint = ComputeFingerprint(report);
        var subject = $"[Nexus Guardian][{severity}][{version}][{component}][{fingerprint}]";
        var body =
            "Nexus Guardian подготовил зашифрованный локальный рапорт.\r\n\r\n" +
            $"ID: {report.Id}\r\n" +
            $"Версия: {report.BrowserVersion}\r\n" +
            $"Компонент: {report.Component} / {report.Stage}\r\n" +
            $"Fingerprint: {fingerprint}\r\n\r\n" +
            "Полный очищенный отчёт находится в зашифрованном вложении .ncrash.\r\n" +
            "Пароли, токены, URL и локальные пользовательские пути в письмо не добавляются.";

        try
        {
            var result = OpenMapiComposer(ownerHandle, subject, body, attachment);
            if (result == 0)
                return new(true, true,
                    "Окно почтового клиента было закрыто. Guardian не может определить, было ли письмо отправлено.",
                    attachment);
            if (result == 1)
                return new(false, true, "Отправка отменена пользователем. Зашифрованное вложение сохранено локально.", attachment);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }

        var mailto = $"mailto:{Recipient}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{attachment}\"") { UseShellExecute = true });
        return new(true, false,
            "Почтовый клиент не поддерживает MAPI-вложения. Guardian открыл черновик и выделил файл .ncrash — прикрепите его вручную.",
            attachment);
    }

    public static string CreateEncryptedAttachment(GuardianReportSnapshot report)
    {
        if (!IsEncryptionReady)
            throw new InvalidOperationException(
                "В сборке отсутствует открытый ключ шифрования рапортов. Выполните scripts/New-CrashReportKey.ps1 и пересоберите браузер.");

        var publicKey = ReadPublicKey();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);

        var plainText = Encoding.UTF8.GetBytes(report.Json);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipherText = new byte[plainText.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plainText, cipherText, tag, AssociatedData);
            var encryptedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
            var keyId = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))
                .ToLowerInvariant()[..16];
            var envelope = new
            {
                SchemaVersion = 1,
                Algorithm,
                KeyId = keyId,
                CreatedUtc = DateTimeOffset.UtcNow,
                ReportId = report.Id,
                EncryptedKey = Convert.ToBase64String(encryptedKey),
                Nonce = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                CipherText = Convert.ToBase64String(cipherText),
                PlainTextSha256 = Convert.ToHexString(SHA256.HashData(plainText)).ToLowerInvariant()
            };

            var outbox = Path.Combine(CrashReportService.VaultPath, "MailOutbox");
            Directory.CreateDirectory(outbox);
            CleanupOutbox(outbox);
            var id = SafeFileToken(report.Id, Guid.NewGuid().ToString("N"));
            var path = Path.Combine(outbox, $"guardian-{id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.ncrash");
            File.WriteAllText(path, JsonSerializer.Serialize(envelope, JsonOptions), new UTF8Encoding(false));
            return path;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainText);
        }
    }

    private static string ReadPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PublicKeyResource)
            ?? throw new InvalidDataException("Открытый ключ шифрования рапортов не встроен в Nexus Monach.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ComputeFingerprint(GuardianReportSnapshot report)
    {
        var source = string.Join('|', report.Component, report.Stage, report.ExceptionType).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..12];
    }

    private static string SafeSubjectToken(string value, string fallback)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            .Take(48).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string SafeFileToken(string value, string fallback)
    {
        var cleaned = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static void CleanupOutbox(string directory)
    {
        try
        {
            var threshold = DateTime.UtcNow.AddDays(-30);
            foreach (var path in Directory.EnumerateFiles(directory, "*.ncrash")
                         .Where(path => File.GetLastWriteTimeUtc(path) < threshold))
                File.Delete(path);
        }
        catch { /* Cleanup must never block preparing a new report. */ }
    }

    private static int OpenMapiComposer(nint ownerHandle, string subject, string body, string attachment)
    {
        var recipient = new MapiRecipDesc
        {
            Reserved = 0,
            RecipientClass = MapiTo,
            Name = Recipient,
            Address = "SMTP:" + Recipient,
            EntryIdSize = 0,
            EntryId = nint.Zero
        };
        var file = new MapiFileDesc
        {
            Reserved = 0,
            Flags = 0,
            Position = -1,
            PathName = attachment,
            FileName = Path.GetFileName(attachment),
            FileType = nint.Zero
        };
        var recipientPointer = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipDesc>());
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDesc>());
        try
        {
            Marshal.StructureToPtr(recipient, recipientPointer, false);
            Marshal.StructureToPtr(file, filePointer, false);
            var message = new MapiMessage
            {
                Reserved = 0,
                Subject = subject,
                NoteText = body,
                MessageType = null,
                DateReceived = null,
                ConversationId = null,
                Flags = 0,
                Originator = nint.Zero,
                RecipientCount = 1,
                Recipients = recipientPointer,
                FileCount = 1,
                Files = filePointer
            };
            return MapiSendMail(nint.Zero, ownerHandle, ref message,
                MapiLogonUi | MapiNewSession | MapiDialog, 0);
        }
        finally
        {
            Marshal.DestroyStructure<MapiRecipDesc>(recipientPointer);
            Marshal.DestroyStructure<MapiFileDesc>(filePointer);
            Marshal.FreeHGlobal(recipientPointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("MAPI32.DLL", EntryPoint = "MAPISendMailW", CharSet = CharSet.Unicode)]
    private static extern int MapiSendMail(
        nint session, nint uiParam, ref MapiMessage message, int flags, int reserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiMessage
    {
        public int Reserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Subject;
        [MarshalAs(UnmanagedType.LPWStr)] public string? NoteText;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MessageType;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DateReceived;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ConversationId;
        public int Flags;
        public nint Originator;
        public int RecipientCount;
        public nint Recipients;
        public int FileCount;
        public nint Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiRecipDesc
    {
        public int Reserved;
        public int RecipientClass;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Address;
        public int EntryIdSize;
        public nint EntryId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFileDesc
    {
        public int Reserved;
        public int Flags;
        public int Position;
        [MarshalAs(UnmanagedType.LPWStr)] public string? PathName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileName;
        public nint FileType;
    }
}
