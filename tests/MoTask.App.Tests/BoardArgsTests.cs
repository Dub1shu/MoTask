using System.Text.Json;
using FluentAssertions;
using MoTask.App.Ai.BoardTools;
using Xunit;

namespace MoTask.App.Tests;

public class BoardArgsTests
{
    private static BoardArgs Args(string json) => new(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Has_DistinguishesMissingFromExplicitNull()
    {
        var args = Args("""{"project":null,"title":"x"}""");

        args.Has("project").Should().BeTrue();
        args.IsExplicitNull("project").Should().BeTrue();
        args.Has("due").Should().BeFalse();
        args.IsExplicitNull("due").Should().BeFalse();
        args.IsExplicitNull("title").Should().BeFalse();
    }

    [Fact]
    public void String_And_Int_ReadTheirOwnKinds()
    {
        var args = Args("""{"title":"やること","task":42,"position":0,"loose":"7"}""");

        args.String("title").Should().Be("やること");
        args.Int("task").Should().Be(42);
        args.Int("position").Should().Be(0);
        args.Int("loose").Should().BeNull();
        args.String("task").Should().BeNull();
        args.Int("missing").Should().BeNull();
    }

    [Fact]
    public void TryRef_AcceptsIdOrName_AndTreatsMissingAndNullAsNoValue()
    {
        Args("""{"column":3}""").TryRef("column", out var byId).Should().BeTrue();
        byId.Should().Be(McpRef.FromId(3));

        Args("""{"column":"進行中"}""").TryRef("column", out var byName).Should().BeTrue();
        byName.Should().Be(McpRef.FromName("進行中"));

        Args("""{}""").TryRef("column", out var missing).Should().BeTrue();
        missing.Should().BeNull();

        Args("""{"column":null}""").TryRef("column", out var nulled).Should().BeTrue();
        nulled.Should().BeNull();
    }

    [Fact]
    public void TryRef_RejectsOtherKinds()
    {
        Args("""{"column":true}""").TryRef("column", out _).Should().BeFalse();
        Args("""{"column":{"id":1}}""").TryRef("column", out _).Should().BeFalse();
        Args("""{"column":1.5}""").TryRef("column", out _).Should().BeFalse();
    }

    [Fact]
    public void TryRefs_AcceptsArraysAndSingleValues()
    {
        Args("""{"labels":["bug",5]}""").TryRefs("labels", out var many).Should().BeTrue();
        many.Should().Equal(McpRef.FromName("bug"), McpRef.FromId(5));

        Args("""{"labels":"bug"}""").TryRefs("labels", out var one).Should().BeTrue();
        one.Should().Equal(McpRef.FromName("bug"));

        Args("""{}""").TryRefs("labels", out var missing).Should().BeTrue();
        missing.Should().BeNull();
    }

    [Fact]
    public void TryRefs_ExplicitNullOrEmptyArray_MeansClearThem()
    {
        Args("""{"labels":null}""").TryRefs("labels", out var nulled).Should().BeTrue();
        nulled.Should().BeEmpty();

        Args("""{"labels":[]}""").TryRefs("labels", out var empty).Should().BeTrue();
        empty.Should().BeEmpty();
    }

    [Fact]
    public void TryRefs_RejectsAnyBadElement()
        => Args("""{"labels":["bug",true]}""").TryRefs("labels", out _).Should().BeFalse();

    [Fact]
    public void TryDate_ReadsIsoDates()
    {
        Args("""{"due":"2026-09-08"}""").TryDate("due", out var due).Should().BeTrue();
        due.Should().Be(new DateOnly(2026, 9, 8));

        Args("""{}""").TryDate("due", out var missing).Should().BeTrue();
        missing.Should().BeNull();

        Args("""{"due":null}""").TryDate("due", out var nulled).Should().BeTrue();
        nulled.Should().BeNull();
    }

    [Fact]
    public void TryDate_RejectsOtherShapes()
    {
        Args("""{"due":"2026/09/08"}""").TryDate("due", out _).Should().BeFalse();
        Args("""{"due":"明日"}""").TryDate("due", out _).Should().BeFalse();
        Args("""{"due":20260908}""").TryDate("due", out _).Should().BeFalse();
    }

    [Fact]
    public void NonObjectArguments_BehaveAsEmpty()
    {
        var args = new BoardArgs(JsonDocument.Parse("[]").RootElement);

        args.Has("title").Should().BeFalse();
        args.String("title").Should().BeNull();
        args.TryRef("column", out var value).Should().BeTrue();
        value.Should().BeNull();
    }
}
