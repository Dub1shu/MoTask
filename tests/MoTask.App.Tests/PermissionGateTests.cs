using FluentAssertions;
using MoTask.App.Ai;
using MoTask.Core.Ai;
using Xunit;

namespace MoTask.App.Tests;

/// <summary>
/// 「RunAsync が返ったあとに承認コールバックが呼ばれない」という契約の実体（Task 5 レビュー由来）。
/// 待ちは必ずテスト側が明示的に解放する。時間切れで通ってしまうテストにしない。
/// </summary>
public class PermissionGateTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly PermissionRequest Ask = new("Bash", """{"command":"ls"}""", "toolu_1");

    [Fact]
    public async Task RequestBeforeClose_ReachesTheInnerHandler_AndItsDecisionComesBack()
    {
        var calls = 0;
        var gate = new PermissionGate((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PermissionDecision.Deny("だめ"));
        });
        gate.SinceLastDecision.Should().BeNull("まだ 1 件も決まっていない");

        var decision = await gate.InvokeAsync(Ask, CancellationToken.None).WaitAsync(Limit);

        calls.Should().Be(1);
        decision.IsAllowed.Should().BeFalse();
        decision.Message.Should().Be("だめ");
        gate.SinceLastDecision.Should().NotBeNull();
    }

    [Fact]
    public async Task RequestAfterClose_IsDenied_WithoutInvokingTheInnerHandler()
    {
        var calls = 0;
        var gate = new PermissionGate((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PermissionDecision.Allow());
        });

        gate.Close();
        var decision = await gate.InvokeAsync(Ask, CancellationToken.None).WaitAsync(Limit);

        calls.Should().Be(0, "閉じたあとの要求を Core へ通すと、確定済みのジョブが Running に蘇る");
        decision.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task WaitIdle_WaitsWhileARequestIsInFlight_AndCompletesWhenItFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new PermissionGate(async (_, _) =>
        {
            entered.SetResult();
            return await release.Task;
        });

        var invoke = gate.InvokeAsync(Ask, CancellationToken.None);
        await entered.Task.WaitAsync(Limit); // ハンドラの中に入ったことを確かめてから待ちを見る

        var idle = gate.WaitIdleAsync(null);
        idle.IsCompleted.Should().BeFalse("ハンドラが走っている間は待ち続ける");

        release.SetResult(PermissionDecision.Allow()); // 解放はテストが明示的に行う（時間切れではない）
        (await invoke.WaitAsync(Limit)).IsAllowed.Should().BeTrue();
        await idle.WaitAsync(Limit);

        gate.WaitIdleAsync(null).IsCompleted.Should().BeTrue("空になったあとの待ちは即座に終わる");
    }

    [Fact]
    public async Task Close_IsSafeToCallTwice()
    {
        var calls = 0;
        var gate = new PermissionGate((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(PermissionDecision.Allow());
        });

        gate.Close();
        gate.Close();

        var decision = await gate.InvokeAsync(Ask, CancellationToken.None).WaitAsync(Limit);
        decision.IsAllowed.Should().BeFalse();
        calls.Should().Be(0);
        await gate.WaitIdleAsync(null).WaitAsync(Limit);
    }
}
