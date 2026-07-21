using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NexusMonach.Models;

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _status = "Загрузка";
    private long _bytesReceived;
    private long _totalBytes;
    private string _riskLevel = "Проверяется";
    private string _securityDetails = "Ожидание завершения загрузки";
    private string _sha256 = string.Empty;
    private string _signatureInfo = "Не проверена";
    private bool _signatureTrusted;
    private bool _requiresOpenConfirmation;

    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set { _bytesReceived = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set { _totalBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); }
    }

    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
    public string ProgressText => TotalBytes <= 0
        ? $"{FormatBytes(BytesReceived)} · {Status}"
        : $"{FormatBytes(BytesReceived)} из {FormatBytes(TotalBytes)} · {Status}";

    public string RiskLevel
    {
        get => _riskLevel;
        set { _riskLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(SecuritySummary)); }
    }

    public string SecurityDetails
    {
        get => _securityDetails;
        set { _securityDetails = value; OnPropertyChanged(); OnPropertyChanged(nameof(SecuritySummary)); }
    }

    public string Sha256
    {
        get => _sha256;
        set { _sha256 = value; OnPropertyChanged(); }
    }

    public string SignatureInfo
    {
        get => _signatureInfo;
        set { _signatureInfo = value; OnPropertyChanged(); OnPropertyChanged(nameof(SecuritySummary)); }
    }

    public bool SignatureTrusted
    {
        get => _signatureTrusted;
        set { _signatureTrusted = value; OnPropertyChanged(); }
    }

    public bool RequiresOpenConfirmation
    {
        get => _requiresOpenConfirmation;
        set { _requiresOpenConfirmation = value; OnPropertyChanged(); }
    }

    public string SecuritySummary => $"Риск: {RiskLevel} · {SecurityDetails} · {SignatureInfo}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatBytes(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
