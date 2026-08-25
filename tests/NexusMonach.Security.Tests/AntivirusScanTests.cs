using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class AntivirusScanTests
{
    [Theory]
    [InlineData(0, "", DownloadScanState.Clean)]
    [InlineData(2, "", DownloadScanState.Threat)]
    [InlineData(2, "Threat: Trojan:Win32/Fake, count 1", DownloadScanState.Threat)]
    [InlineData(2, "ERROR: service disabled", DownloadScanState.Unavailable)]
    [InlineData(1, "anything", DownloadScanState.Unavailable)]
    [InlineData(-2147024894, "error", DownloadScanState.Unavailable)]
    public void Classify_Maps_Defender_Exit_Codes(int exitCode, string output, DownloadScanState expected)
        => Assert.Equal(expected, AntivirusScanService.Classify(exitCode, output));

    [Fact]
    public void ScanStatusText_Covers_All_States()
    {
        foreach (var state in Enum.GetValues<DownloadScanState>())
            Assert.False(string.IsNullOrWhiteSpace(AntivirusScanService.ScanStatusText(state)));
    }

    [Fact]
    public void SecuritySummary_Includes_Scan_Result_When_Present()
    {
        var item = new DownloadItem();
        Assert.DoesNotContain("Антивирус", item.SecuritySummary);

        item.ScanState = DownloadScanState.Clean;
        Assert.Contains("Антивирус: угроз не обнаружено", item.SecuritySummary);

        item.ScanState = DownloadScanState.Threat;
        Assert.Contains("Антивирус: обнаружена угроза", item.SecuritySummary);
    }

    [Fact]
    public void SetAssessment_Reports_Risk_Without_Requesting_Confirmation()
    {
        var item = new DownloadItem { FileName = "setup.exe" };
        DownloadSecurityService.SetAssessment(item,
            DownloadSecurityService.Assess("setup.pdf.exe", "https://example.com/file/setup.pdf.exe"));
        Assert.Equal("высокий", item.RiskLevel);
        Assert.Contains("Двойное расширение", item.SecurityDetails);
    }
}
