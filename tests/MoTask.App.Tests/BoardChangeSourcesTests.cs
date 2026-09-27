using FluentAssertions;
using MoTask.App.Ai;
using MoTask.App.Tests.Fakes;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>ボード画面を通らない書き込み(MCP と計画の仕分け)を、1 本の通知にまとめる。</summary>
public class BoardChangeSourcesTests
{
    private readonly FakeBoardChangeSource _mcp = new();
    private readonly FakePlanningService _planning = new();
    private readonly BoardChangeSources _sources;
    private int _raised;

    public BoardChangeSourcesTests()
    {
        _sources = new BoardChangeSources(_mcp, _planning);
        _sources.BoardChanged += (_, _) => _raised++;
    }

    [Fact]
    public void RelaysMcpWrites()
    {
        _mcp.RaiseBoardChanged();

        _raised.Should().Be(1);
    }

    [Fact]
    public void RelaysPlanningTriage()
    {
        _planning.RaiseBoardChanged();

        _raised.Should().Be(1);
    }
}
