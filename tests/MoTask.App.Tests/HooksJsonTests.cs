using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// --settings に渡すファイル。形が崩れると CLI が黙ってフックを無視し、盤面が一切動かなくなる。
/// 生成物の形をここで固定する（仕様 §7, §13）。
/// </summary>
public class HooksJsonTests
{
    private const string Exe = @"C:\Program Files\MoTask\hooks\MoTask.Hooks.exe";
    private const string Events = @"C:\work\jobs\0042-見積り\events.jsonl";

    [Fact]
    public void Build_WiresTheSameCommandToTheFourEvents()
    {
        using var doc = JsonDocument.Parse(HooksJson.Build(Exe, Events));

        var hooks = doc.RootElement.GetProperty("hooks");
        hooks.EnumerateObject().Select(p => p.Name).Should()
            .Equal("SessionStart", "PostToolUse", "Stop", "SessionEnd");

        foreach (var entry in hooks.EnumerateObject())
        {
            var command = entry.Value[0].GetProperty("hooks")[0];
            command.GetProperty("type").GetString().Should().Be("command");
            command.GetProperty("command").GetString().Should().Be($"\"{Exe}\" \"{Events}\"");
        }
    }

    [Fact]
    public void Build_StaysReadableForHumans()
    {
        var json = HooksJson.Build(Exe, Events);

        // 人が開いて読めること（\u005C や \u30xx だらけにしない）。JSON としての \\ は残る。
        json.Should().Contain("MoTask.Hooks.exe").And.NotContain("u005C").And.NotContain("u898B");
    }
}
