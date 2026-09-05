using System.Text.Json;
using Nexus.Guardian;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Стартовая самодиагностика и самолечение Guardian (2.9.54): порядок
/// «верификация до любой ветки», счётчик попыток применения, rollback,
/// карантин неисправного pending, append-only журнал ошибок применения.
/// </summary>
public sealed class StartupHealthCheckTests
{
    // --- DecideLaunch: верификация первая, запуск без неё запрещён ---

    [Fact]
    public void DecideLaunch_VerifiedWithoutPending_LaunchesBrowser()
    {
        var action = Program.DecideLaunch(Result(IntegrityState.Verified), pendingReady: false);
        Assert.Equal(LaunchAction.LaunchBrowser, action);
    }

    [Fact]
    public void DecideLaunch_VerifiedWithPending_AppliesUpdateFirst()
    {
        var action = Program.DecideLaunch(Result(IntegrityState.Verified), pendingReady: true);
        Assert.Equal(LaunchAction.ApplyPendingUpdate, action);
    }

    [Fact]
    public void DecideLaunch_NonCriticalWithPending_SelfHeals()
    {
        // Самовосстановление из безопасного режима: обновление содержит фикс.
        var action = Program.DecideLaunch(Result(IntegrityState.NonCriticalMismatch), pendingReady: true);
        Assert.Equal(LaunchAction.ApplyPendingUpdate, action);
    }

    [Fact]
    public void DecideLaunch_CriticalWithValidPending_SelfHeals()
    {
        var action = Program.DecideLaunch(Result(IntegrityState.CriticalMismatch), pendingReady: true);
        Assert.Equal(LaunchAction.ApplyPendingUpdate, action);
    }

    [Fact]
    public void DecideLaunch_CriticalWithoutPending_Blocks()
    {
        var action = Program.DecideLaunch(Result(IntegrityState.CriticalMismatch), pendingReady: false);
        Assert.Equal(LaunchAction.Block, action);
    }

    [Fact]
    public void DecideLaunch_InvalidSignatureWithoutPending_Blocks()
    {
        var action = Program.DecideLaunch(Result(IntegrityState.InvalidSignature), pendingReady: false);
        Assert.Equal(LaunchAction.Block, action);
    }

    // --- Вердикт самодиагностики ---

    [Fact]
    public void HealthDecide_AllOk_Ok()
    {
        var verdict = StartupHealthCheck.Decide(new[]
        {
            new HealthCheckItem("integrity", HealthStatus.Ok, ""),
            new HealthCheckItem("webview2", HealthStatus.Ok, "")
        });
        Assert.Equal(HealthVerdict.Ok, verdict);
    }

    [Fact]
    public void HealthDecide_AnyWarn_Warn()
    {
        var verdict = StartupHealthCheck.Decide(new[]
        {
            new HealthCheckItem("integrity", HealthStatus.Ok, ""),
            new HealthCheckItem("disk", HealthStatus.Warn, "мало места")
        });
        Assert.Equal(HealthVerdict.Warn, verdict);
    }

    [Fact]
    public void HealthDecide_AnyFail_FailEvenWithWarns()
    {
        var verdict = StartupHealthCheck.Decide(new[]
        {
            new HealthCheckItem("integrity", HealthStatus.Ok, ""),
            new HealthCheckItem("disk", HealthStatus.Warn, ""),
            new HealthCheckItem("webview2", HealthStatus.Fail, "нет runtime")
        });
        Assert.Equal(HealthVerdict.Fail, verdict);
    }

    [Fact]
    public void HealthCompact_FailListsProblemIds()
    {
        var compact = StartupHealthCheck.BuildCompact(HealthVerdict.Fail, new[]
        {
            new HealthCheckItem("integrity", HealthStatus.Ok, ""),
            new HealthCheckItem("webview2", HealthStatus.Fail, "нет runtime")
        });
        Assert.Equal("fail:webview2", compact);
    }

    // --- Ограничение попыток применения ---

    [Theory]
    [InlineData(1, "Retry")]
    [InlineData(2, "Retry")]
    [InlineData(3, "Quarantine")]
    [InlineData(5, "Quarantine")]
    public void ApplyFailure_LimitedToThreeAttempts(int attempts, string expected)
    {
        Assert.Equal(expected, SilentUpdateCoordinator.DecideApplyFailure(attempts).ToString());
    }

    // --- Журнал ошибок применения: append-only, история не стирается ---

    [Fact]
    public void ApplyErrorLog_SecondAttemptKeepsFirstEntry()
    {
        using var fixture = new DirectoryFixture();
        SilentUpdateCoordinator.AppendApplyError(fixture.GuardianRoot, 1, "2.9.54",
            new IOException("файл занят"));
        SilentUpdateCoordinator.AppendApplyError(fixture.GuardianRoot, 2, "2.9.54",
            new UnauthorizedAccessException("доступ запрещён"));

        var lines = File.ReadAllLines(Path.Combine(
            fixture.GuardianRoot, "Updates", "apply-error.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("attempt=1", lines[0]);
        Assert.Contains("attempt=2", lines[1]);
        Assert.Contains("файл занят", lines[0]);
        Assert.Contains("доступ запрещён", lines[1]);
    }

    // --- Rollback: копия до применения, восстановление после провала ---

    [Fact]
    public void Rollback_RestoresFilesDestroyedByFailedApply()
    {
        using var fixture = new DirectoryFixture();
        var target = Path.Combine(fixture.RootDirectory, "target");
        var staging = Path.Combine(fixture.RootDirectory, "staging");
        var rollback = Path.Combine(fixture.RootDirectory, "rollback");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(target, "a.txt"), "старая-версия-а");
        File.WriteAllText(Path.Combine(target, "sub", "b.txt"), "старая-версия-б");
        WriteManifest(staging, "2.9.54", "a.txt", "sub/b.txt");

        SilentUpdateCoordinator.CreateRollback(staging, target, rollback);
        Assert.True(File.Exists(Path.Combine(rollback, "a.txt")));
        Assert.True(File.Exists(Path.Combine(rollback, "sub", "b.txt")));

        // Применение упало посреди копирования: новые файлы уже в target.
        File.WriteAllText(Path.Combine(target, "a.txt"), "новая-битая");
        File.Delete(Path.Combine(target, "sub", "b.txt"));

        var restored = SilentUpdateCoordinator.TryRestoreRollback(rollback, target);

        Assert.True(restored);
        Assert.Equal("старая-версия-а", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("старая-версия-б", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
    }

    [Fact]
    public void QuarantinePending_BreaksTheRetryLoopAndKeepsEvidence()
    {
        using var fixture = new DirectoryFixture();
        var target = Path.Combine(fixture.RootDirectory, "target");
        var staging = Path.Combine(fixture.RootDirectory, "staging");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(staging);
        var pending = new PendingGuardianUpdate
        {
            Version = "2.9.54",
            StagingDirectory = staging,
            TargetDirectory = target,
            Attempts = 3,
            LastError = "тест"
        };
        var updates = Path.Combine(fixture.GuardianRoot, "Updates");
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, "pending-update.json"),
            JsonSerializer.Serialize(pending));

        SilentUpdateCoordinator.QuarantinePending(fixture.GuardianRoot, pending);

        // Петля «старт → апликатор → падение» прервана: pending удалён,
        // причина (попытки + последняя ошибка) сохранена в rejected-файле.
        Assert.False(File.Exists(Path.Combine(updates, "pending-update.json")));
        var rejected = JsonSerializer.Deserialize<PendingGuardianUpdate>(
            File.ReadAllText(Path.Combine(updates, "pending-update.rejected.json")));
        Assert.Equal(3, rejected!.Attempts);
        Assert.Equal("тест", rejected.LastError);
    }

    private static IntegrityResult Result(IntegrityState state) => new() { State = state };

    private static void WriteManifest(string staging, string version, params string[] files)
    {
        var manifest = new IntegrityManifest
        {
            Version = version,
            Files = files.Select(path => new IntegrityFile
            {
                Path = path,
                Length = 16,
                Critical = true
            }).ToList()
        };
        File.WriteAllText(Path.Combine(staging, IntegrityVerifier.ManifestName),
            JsonSerializer.Serialize(manifest));
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string RootDirectory { get; } = Path.Combine(
            Path.GetTempPath(), "NexusStartupHealthTests", Guid.NewGuid().ToString("N"));
        public string GuardianRoot => Path.Combine(RootDirectory, "Guardian");

        public DirectoryFixture() => Directory.CreateDirectory(RootDirectory);

        public void Dispose()
        {
            try { Directory.Delete(RootDirectory, recursive: true); }
            catch { }
        }
    }
}
