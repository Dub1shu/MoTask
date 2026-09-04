using MoTask.Core.Abstractions;

namespace MoTask.Core.Tests.Fakes;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
    public DateOnly Today { get; set; } = new(2026, 9, 4);

    public void Advance(TimeSpan by) => UtcNow += by;
}
