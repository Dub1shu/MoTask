using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MoTask.Core.Abstractions;
using MoTask.Core.Ai;
using MoTask.Core.Services;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// BoardService と AiJobService は 1 つの DbContext を共有し、同じ OperationGate で直列化される。
/// ゲートが 2 つに割れても例外は出ず、離れた場所で「A second operation was started on this context」が
/// 散発するだけなので、配線そのものをここで固定する。
/// </summary>
public class HostWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MoTaskTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildHost_GivesBoardServiceAndAiJobService_TheSameOperationGate()
    {
        Directory.CreateDirectory(_dir);
        using var host = App.BuildHost(Path.Combine(_dir, "motask.db"));

        var gate = host.Services.GetRequiredService<OperationGate>();
        GateOf(host.Services.GetRequiredService<IBoardService>()).Should().BeSameAs(gate);
        GateOf(host.Services.GetRequiredService<IAiJobService>()).Should().BeSameAs(gate);
    }

    [Fact]
    public void BuildHost_GivesMorningServiceTheSameOperationGate()
    {
        Directory.CreateDirectory(_dir);
        using var host = App.BuildHost(Path.Combine(_dir, "motask.db"));

        GateOf(host.Services.GetRequiredService<IMorningService>())
            .Should().BeSameAs(host.Services.GetRequiredService<OperationGate>());
    }

    [Fact]
    public void BuildHost_ResolvesTheMorningPlanViewModel()
    {
        Directory.CreateDirectory(_dir);
        using var host = App.BuildHost(Path.Combine(_dir, "motask.db"));

        host.Services.GetRequiredService<ViewModels.MorningPlanViewModel>().Should().NotBeNull();
    }

    /// <summary>ゲートは公開されていないので、この不変条件だけリフレクションで見る。</summary>
    private static OperationGate GateOf(object service)
        => (OperationGate)service.GetType()
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    [Fact]
    public void BuildHost_GivesTheMcpServerAndTheViewModel_TheSameBoardToolHost()
    {
        Directory.CreateDirectory(_dir);
        using var host = App.BuildHost(Path.Combine(_dir, "motask.db"));

        var tools = host.Services.GetRequiredService<MoTask.App.Ai.BoardTools.BoardToolHost>();
        host.Services.GetRequiredService<MoTask.App.Ai.IBoardChangeSource>().Should().BeSameAs(tools);
        host.Services.GetRequiredService<MoTask.App.Ai.MoTaskMcpServer>().Should().NotBeNull();
        // ViewModel まで実際に解決して、購読側を含む DI グラフが組み上がることを見る。
        host.Services.GetRequiredService<MoTask.App.ViewModels.BoardViewModel>().Should().NotBeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
