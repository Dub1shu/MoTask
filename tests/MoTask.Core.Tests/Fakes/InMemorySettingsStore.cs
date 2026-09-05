using MoTask.Core.Ai;

namespace MoTask.Core.Tests.Fakes;

public sealed class InMemorySettingsStore : IAiSettingsStore
{
    public AiSettings Settings { get; set; } = AiSettings.Default();
    public int SaveCount { get; private set; }

    public AiSettings Load() => Settings;

    public void Save(AiSettings settings)
    {
        Settings = settings;
        SaveCount++;
    }
}
