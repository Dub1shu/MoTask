using System.Collections.Concurrent;
using System.IO;
using System.Text;
using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

public class JobEventWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));
    private readonly string _events;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ConcurrentQueue<string> _problems = new();
    private readonly JobEventWatcher _watcher = new(TimeSpan.FromMilliseconds(20));

    public JobEventWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        _events = Path.Combine(_dir, "events.jsonl");
    }

    private JobEventSubscription Subscription(int skipLines = 0) => new(
        JobId: 1, EventsPath: _events, SkipLines: skipLines,
        OnLine: line => { _lines.Enqueue(line); return Task.CompletedTask; },
        OnProblem: message => { _problems.Enqueue(message); return Task.CompletedTask; });

    private static void Append(string path, params string[] lines)
        => File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));

    /// <summary>ポーリングなので、条件が満たされるまで少し待つ。</summary>
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        condition().Should().BeTrue("5 秒以内に条件が満たされるはず");
    }

    [Fact]
    public async Task Follow_PicksUpLinesAppendedAfterItStarted()
    {
        _watcher.Follow(Subscription());

        Append(_events, "{\"a\":1}", "{\"a\":2}");

        await EventuallyAsync(() => _lines.Count == 2);
        _lines.Should().Equal("{\"a\":1}", "{\"a\":2}");
    }

    [Fact]
    public async Task Follow_WaitsForAFileThatDoesNotExistYet()
    {
        _watcher.Follow(Subscription());
        await Task.Delay(60);

        Append(_events, "{\"late\":true}");

        await EventuallyAsync(() => _lines.Count == 1);
        _problems.Should().BeEmpty("まだ書かれていないだけなので、問題ではない");
    }

    [Fact]
    public async Task Follow_SkipsTheLinesAlreadyRecorded()
    {
        Append(_events, "{\"a\":1}", "{\"a\":2}", "{\"a\":3}");

        _watcher.Follow(Subscription(skipLines: 2));

        await EventuallyAsync(() => _lines.Count == 1);
        _lines.Should().Equal("{\"a\":3}");
    }

    [Fact]
    public async Task Follow_IgnoresAPartiallyWrittenTrailingLine()
    {
        _watcher.Follow(Subscription());
        File.AppendAllText(_events, "{\"whole\":1}\n{\"half\":", new UTF8Encoding(false));

        await EventuallyAsync(() => _lines.Count == 1);
        await Task.Delay(60);
        _lines.Should().Equal("{\"whole\":1}");

        File.AppendAllText(_events, "2}\n", new UTF8Encoding(false));

        await EventuallyAsync(() => _lines.Count == 2);
        _lines.Should().Equal("{\"whole\":1}", "{\"half\":2}");
    }

    [Fact]
    public async Task Follow_ReadsJapaneseAsUtf8()
    {
        _watcher.Follow(Subscription());

        Append(_events, "{\"last_assistant_message\":\"見積りをまとめました\"}");

        await EventuallyAsync(() => _lines.Count == 1);
        _lines.Single().Should().Contain("見積りをまとめました");
    }

    [Fact]
    public async Task Follow_ReportsWhenTheFileDisappears()
    {
        _watcher.Follow(Subscription());
        Append(_events, "{\"a\":1}");
        await EventuallyAsync(() => _lines.Count == 1);

        File.Delete(_events);

        await EventuallyAsync(() => _problems.Count == 1);
        _problems.Single().Should().Contain(_events);
    }

    [Fact]
    public async Task StopFollowing_StopsDelivering()
    {
        _watcher.Follow(Subscription());
        Append(_events, "{\"a\":1}");
        await EventuallyAsync(() => _lines.Count == 1);

        _watcher.StopFollowing(1);
        Append(_events, "{\"a\":2}");
        await Task.Delay(120);

        _lines.Should().HaveCount(1);
    }

    [Fact]
    public void StopFollowing_IsSafeForAJobThatWasNeverFollowed()
    {
        _watcher.Invoking(w => w.StopFollowing(999)).Should().NotThrow();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
