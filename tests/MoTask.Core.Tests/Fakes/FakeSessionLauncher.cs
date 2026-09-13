using MoTask.Core;
using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

/// <summary>実起動はしない。組み立てた要求を記録して、成否だけテストが決める。</summary>
public sealed class FakeSessionLauncher : ISessionLauncher
{
    public Result Availability { get; set; } = Result.Ok();
    public Result? BuildFailure { get; set; }
    public Result? LaunchFailure { get; set; }
    public List<SessionLaunchRequest> Requests { get; } = new();
    public List<TerminalCommand> Launched { get; } = new();

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
}
