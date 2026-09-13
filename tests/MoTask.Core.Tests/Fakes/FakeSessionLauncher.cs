using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>実起動はしない。組み立てた要求を記録して、成否だけテストが決める。</summary>
public sealed class FakeSessionLauncher : ISessionLauncher
{
    public Result Availability { get; set; } = Result.Ok();
    public Result? BuildFailure { get; set; }
    /// <summary>Launch / LaunchOwned のどちらも、これが入っていれば失敗する。</summary>
    public Result? LaunchFailure { get; set; }
    public List<SessionLaunchRequest> Requests { get; } = new();
    public List<TerminalCommand> Launched { get; } = new();

    /// <summary>所有して起こした分（ownerId 付き）。</summary>
    public List<(int OwnerId, TerminalCommand Command)> LaunchedOwned { get; } = new();

    /// <summary>CloseOwned を呼ばれた ownerId の履歴（順序どおり）。</summary>
    public List<int> Closed { get; } = new();

    /// <summary>TryReattach を頼まれた材料の履歴。</summary>
    public List<(int OwnerId, int ProcessId, DateTime StartedAt)> Reattached { get; } = new();

    /// <summary>TryReattach の戻り値。掛け直しに失敗する筋をテストが作れる。</summary>
    public bool ReattachSucceeds { get; set; } = true;

    /// <summary>LaunchOwned が返す pid と開始時刻。</summary>
    public OwnedSession Session { get; set; } = new(4242, new DateTime(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc));

    public event EventHandler<int>? OwnedSessionExited;

    public Result CheckAvailable() => Availability;

    public Result<TerminalCommand> BuildCommand(SessionLaunchRequest request)
    {
        Requests.Add(request);
        if (BuildFailure is { } failure) return Result.Fail<TerminalCommand>(failure.Error!);
        var switches = request.CloseOnExit ? "/c" : "/k";
        return Result.Ok(new TerminalCommand("cmd.exe", $"{switches} claude", request.WorkingDirectory));
    }

    public Result Launch(TerminalCommand command)
    {
        if (LaunchFailure is { } failure) return failure;
        Launched.Add(command);
        return Result.Ok();
    }

    public Result<OwnedSession> LaunchOwned(int ownerId, TerminalCommand command)
    {
        if (LaunchFailure is { } failure) return Result.Fail<OwnedSession>(failure.Error!);
        Launched.Add(command);
        LaunchedOwned.Add((ownerId, command));
        return Result.Ok(Session);
    }

    public void CloseOwned(int ownerId) => Closed.Add(ownerId);

    public bool TryReattach(int ownerId, int processId, DateTime startedAt)
    {
        Reattached.Add((ownerId, processId, startedAt));
        return ReattachSucceeds;
    }

    /// <summary>端末が先に死んだことにする（人が × で閉じた・claude が落ちた）。</summary>
    public void RaiseExited(int ownerId) => OwnedSessionExited?.Invoke(this, ownerId);
}
