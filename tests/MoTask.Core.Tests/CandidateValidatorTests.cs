using FluentAssertions;
using MoTask.Core.Model;
using MoTask.Core.Planning;
using Xunit;

namespace MoTask.Core.Tests;

/// <summary>
/// 候補 1 件の検証（仕様 §6）。MoTask と Claude の接点なので、通る形と落ちる理由を固定する。
/// PlanningResultReaderTests の候補まわりをここへ移植したもの（JSON Lines 読みの分だけ落ちている）。
/// </summary>
public class CandidateValidatorTests
{
    private static CandidateInput Input(
        string externalId = "outlook:AAMkAD001", string source = "Outlook",
        string title = "請求先情報を更新する", string evidence = "「9月8日までに」",
        string action = "register", int? mergeTarget = null)
        => new(externalId, source, title, evidence,
            From: "顧客A 山本さん", Link: "https://outlook.office.com/mail/id/AAMkAD001",
            Reasoning: "依頼が明確で期限の記述あり",
            ReceivedAt: new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc),
            SuggestedDueDate: new DateOnly(2026, 9, 8), SuggestedProject: "顧客A",
            SuggestedAction: action, MergeTargetTaskId: mergeTarget);

    [Fact]
    public void Validate_MapsEveryField()
    {
        var result = CandidateValidator.Validate(Input());

        result.IsSuccess.Should().BeTrue(result.Error);
        var record = result.Value!;
        record.ExternalId.Should().Be("outlook:AAMkAD001");
        record.Source.Should().Be("Outlook");
        record.Title.Should().Be("請求先情報を更新する");
        record.Evidence.Should().Be("「9月8日までに」");
        record.From.Should().Be("顧客A 山本さん");
        record.Link.Should().Be("https://outlook.office.com/mail/id/AAMkAD001");
        record.Reasoning.Should().Be("依頼が明確で期限の記述あり");
        record.ReceivedAt.Should().Be(new DateTime(2026, 9, 6, 22, 42, 0, DateTimeKind.Utc));
        record.SuggestedDueDate.Should().Be(new DateOnly(2026, 9, 8));
        record.SuggestedProject.Should().Be("顧客A");
        record.SuggestedAction.Should().Be(TriageAction.Register);
        record.MergeTargetTaskId.Should().BeNull();
    }

    [Theory]
    [InlineData("register", TriageAction.Register)]
    [InlineData("later", TriageAction.Later)]
    [InlineData("reject", TriageAction.Reject)]
    public void Validate_AcceptsTheActionsThatNeedNoTarget(string action, TriageAction expected)
    {
        CandidateValidator.Validate(Input(action: action)).Value!.SuggestedAction.Should().Be(expected);
    }

    [Fact]
    public void Validate_KeepsTheMergeTarget()
    {
        var record = CandidateValidator.Validate(Input(action: "merge", mergeTarget: 45)).Value!;

        record.SuggestedAction.Should().Be(TriageAction.Merge);
        record.MergeTargetTaskId.Should().Be(45);
    }

    [Fact]
    public void Validate_AcceptsAnySourceString()
    {
        CandidateValidator.Validate(Input(source: "社内ポータル")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_TrimsTheTextFields()
    {
        CandidateValidator.Validate(Input(title: "  余白つき  ")).Value!.Title.Should().Be("余白つき");
    }

    [Theory]
    [InlineData("externalId")]
    [InlineData("source")]
    [InlineData("title")]
    public void Validate_NamesTheMissingRequiredField(string field)
    {
        var input = field switch
        {
            "externalId" => Input(externalId: "  "),
            "source" => Input(source: ""),
            _ => Input(title: ""),
        };

        var result = CandidateValidator.Validate(input);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(string.Format(Messages.CandidateFieldRequiredFormat, field));
    }

    /// <summary>根拠の無い候補は人が判断できない。理由は「引用してください」まで言う（仕様 §6）。</summary>
    [Fact]
    public void Validate_RefusesACandidateWithoutEvidence()
    {
        var result = CandidateValidator.Validate(Input(evidence: ""));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.CandidateEvidenceRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("REGISTER")]
    [InlineData("archive")]
    public void Validate_RefusesAnActionOutsideTheFour(string action)
    {
        var result = CandidateValidator.Validate(Input(action: action));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.CandidateActionInvalid);
    }

    [Fact]
    public void Validate_RefusesMergeWithoutATarget()
    {
        var result = CandidateValidator.Validate(Input(action: "merge"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Messages.CandidateMergeTargetMissing);
    }

    [Fact]
    public void Actions_AreTheFourFixedOnes()
        => CandidateValidator.Actions.Should().Equal("register", "merge", "later", "reject");
}
