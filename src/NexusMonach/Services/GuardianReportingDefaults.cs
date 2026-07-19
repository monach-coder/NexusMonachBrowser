using System.Text.Json;

namespace NexusMonach.Services;

public static class GuardianReportingDefaults
{
    private const string FileName = "guardian-reporting.json";
    private static readonly Lazy<ReportingDefaults> Current = new(Read);

    public static string Endpoint => Current.Value.Endpoint;
    public static string IngestKey => Current.Value.IngestKey;
    public static string Mode => Current.Value.Mode;

    private static ReportingDefaults Read()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<ReportingDefaults>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { return new(); }
    }

    private sealed class ReportingDefaults
    {
        public string Endpoint { get; set; } = string.Empty;
        public string IngestKey { get; set; } = string.Empty;
        public string Mode { get; set; } = "ask";
    }
}
