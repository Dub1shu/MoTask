using FluentAssertions;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.Data.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Load_WhenFileIsMissing_ReturnsDefaults()
    {
        var store = new JsonAiSettingsStore(PathOf("settings.json"));
        store.Load().Should().Be(AiSettings.Default());
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_AndCreatesTheDirectory()
    {
        var store = new JsonAiSettingsStore(PathOf("settings.json"));
        var settings = new AiSettings(@"C:\work", 2, @"C:\tools\claude.exe", "claude-sonnet-5", 20, AiSettings.DefaultPermissionMode, null);

        store.Save(settings);

        File.Exists(PathOf("settings.json")).Should().BeTrue();
        new JsonAiSettingsStore(PathOf("settings.json")).Load().Should().Be(settings);
    }

    [Fact]
    public void Load_WithBrokenJson_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("settings.json"), "{ not json");
        new JsonAiSettingsStore(PathOf("settings.json")).Load().Should().Be(AiSettings.Default());
    }

    [Fact]
    public void Load_WithMissingFields_FillsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf("settings.json"), """{"MaxConcurrentJobs": 5}""");
        var loaded = new JsonAiSettingsStore(PathOf("settings.json")).Load();
        loaded.MaxConcurrentJobs.Should().Be(5);
        loaded.DefaultWorkingDirectory.Should().Be(AiSettings.Default().DefaultWorkingDirectory);
        loaded.MaxTurns.Should().Be(AiSettings.DefaultMaxTurns);
        loaded.Model.Should().BeNull();
    }

    [Fact]
    public void Save_CalledAgain_ReplacesThePreviousFile_AndRoundTrips()
    {
        var store = new JsonAiSettingsStore(PathOf("settings.json"));
        var first = new AiSettings(@"C:\work", 2, @"C:\tools\claude.exe", "claude-sonnet-5", 20, AiSettings.DefaultPermissionMode, null);
        var second = new AiSettings(@"C:\other", 4, null, null, 30, AiSettings.DefaultPermissionMode, null);

        store.Save(first);
        store.Save(second);

        new JsonAiSettingsStore(PathOf("settings.json")).Load().Should().Be(second);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var store = new JsonAiSettingsStore(PathOf("settings.json"));

        store.Save(new AiSettings(@"C:\work", 2, @"C:\tools\claude.exe", "claude-sonnet-5", 20, AiSettings.DefaultPermissionMode, null));

        Directory.GetFiles(_dir).Should().Equal(PathOf("settings.json"));
        File.Exists(PathOf("settings.json.tmp")).Should().BeFalse();
    }

    [Fact]
    public void Load_FallsBackWhenPermissionModeIsUnknown()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"PermissionMode":"すきなように","TerminalCommandTemplate":"pwsh -c {command}"}""");

        var settings = new JsonAiSettingsStore(path).Load();

        settings.PermissionMode.Should().Be("auto");
        settings.TerminalCommandTemplate.Should().Be("pwsh -c {command}");
    }

    [Fact]
    public void Save_RoundTripsPermissionModeAndTemplate()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new JsonAiSettingsStore(path);

        store.Save(AiSettings.Default() with { PermissionMode = "plan", TerminalCommandTemplate = "wt -d {cwd} {command}" });

        var loaded = new JsonAiSettingsStore(path).Load();
        loaded.PermissionMode.Should().Be("plan");
        loaded.TerminalCommandTemplate.Should().Be("wt -d {cwd} {command}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
