using System.IO;
using System.Text.Json;
using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Morning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// MoTask と Claude の唯一の接点（仕様 §8）。CLI や指示文が形を変えたらここが赤くなる。
/// fixture は初回の実機確認のあと、本物の result/ に差し替えること（README のチェックリスト参照）。
/// </summary>
public class MorningResultReaderTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MorningResult ReadFixtures(string candidates, string plan)
        => MorningResultReader.Read(Fixture(candidates), Fixture(plan));

    [Fact]
    public void Read_TakesEveryWellFormedCandidate()
    {
        var result = ReadFixtures("morning-candidates.jsonl", "morning-plan.json");

        result.Candidates.Should().HaveCount(3);
        result.DiscardedLines.Should().Be(0);
        result.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void Read_MapsEveryFieldOfACandidate()
    {
        var first = ReadFixtures("morning-candidates.jsonl", "morning-plan.json").Candidates[0];

        first.ExternalId.Should().Be("outlook:AAMkAD001");
        first.Source.Should().Be("Outlook");
        first.From.Should().Be("顧客A 山本さん");
        first.Title.Should().Be("請求先情報を更新する");
        first.Evidence.Should().Contain("9月8日までに");
        first.Link.Should().Be("https://outlook.office.com/mail/id/AAMkAD001");
        first.Reasoning.Should().Contain("依頼が明確");
        first.SuggestedProject.Should().Be("顧客A");
        first.SuggestedAction.Should().Be(TriageAction.Register);
        first.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
        first.MergeTargetTaskId.Should().BeNull();
        // +09:00 の 07:42 は UTC の前日 22:42
        first.ReceivedAt.Should().Be(new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Read_KeepsTheMergeTarget()
    {
        var merge = ReadFixtures("morning-candidates.jsonl", "morning-plan.json").Candidates[1];

        merge.SuggestedAction.Should().Be(TriageAction.Merge);
        merge.MergeTargetTaskId.Should().Be(45);
    }

    [Fact]
    public void Read_AcceptsAnySourceString()
    {
        var gmail = ReadFixtures("morning-candidates.jsonl", "morning-plan.json").Candidates[2];

        gmail.Source.Should().Be("Gmail", "取り込み元は列挙しない（仕様 §4）");
        gmail.SuggestedAction.Should().Be(TriageAction.Later);
        gmail.SuggestedProject.Should().BeEmpty("欠けている項目は空扱い");
        gmail.SuggestedDueDate.Should().BeNull();
    }

    [Fact]
    public void Read_DropsBadLinesAndCountsThem()
    {
        var result = ReadFixtures("morning-candidates-broken.jsonl", "morning-plan.json");

        result.Candidates.Should().ContainSingle()
            .Which.ExternalId.Should().Be("outlook:AAMkAD100");
        result.DiscardedLines.Should().Be(4, "JSON 崩れ・根拠なし・統合先なし・重複の 4 行");
        result.IsUsable.Should().BeTrue("1 行の崩れで朝が全滅しない");
    }

    [Fact]
    public void Read_DoesNotCountBlankLinesAsDiscarded()
    {
        var result = MorningResultReader.Read("\n\n   \n", Fixture("morning-plan.json"));

        result.Candidates.Should().BeEmpty();
        result.DiscardedLines.Should().Be(0);
    }

    [Fact]
    public void Read_KeepsThePlanVerbatimWhenItIsValid()
    {
        var raw = Fixture("morning-plan.json");

        var result = MorningResultReader.Read(null, raw);

        result.PlanJson.Should().Be(raw.Trim(), "検証済みの生 JSON を 1 カラムに持つ（仕様 §9）");
        JsonDocument.Parse(result.PlanJson).RootElement
            .GetProperty("groups").GetArrayLength().Should().Be(4);
    }

    [Theory]
    [InlineData("morning-plan-broken.json")]
    public void Read_RejectsAPlanWithAnUnknownGroupKey(string fixture)
        => MorningResultReader.Read(null, Fixture(fixture)).PlanJson.Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{壊れた")]
    [InlineData("[]")]
    [InlineData("{\"date\":\"2026-09-07\"}")]
    [InlineData("{\"groups\":{}}")]
    [InlineData("{\"groups\":[{\"key\":\"today\"}]}")]
    public void Read_RejectsAnUnusablePlan(string? plan)
    {
        var result = MorningResultReader.Read(null, plan);

        result.PlanJson.Should().BeEmpty();
        result.IsUsable.Should().BeFalse("プランが読めない実行は Failed（仕様 §8）");
    }

    [Fact]
    public void Read_SucceedsWithZeroCandidates_WhenThePlanIsValid()
    {
        var result = MorningResultReader.Read("", Fixture("morning-plan.json"));

        result.Candidates.Should().BeEmpty();
        result.IsUsable.Should().BeTrue("コネクタ未認証や候補が無い朝は失敗ではない（仕様 §8）");
    }

    [Fact]
    public void PlanGroupKeys_AreTheFourFixedOnes()
        => MorningResultReader.PlanGroupKeys.Should().Equal("today", "ifTime", "aiReady", "waiting");
}
