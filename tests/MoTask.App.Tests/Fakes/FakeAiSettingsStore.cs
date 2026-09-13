using MoTask.Core.Ai;

namespace MoTask.App.Tests.Fakes;

/// <summary>ファイルは触らない。返す設定はテストが差し替え、保存された設定を記録するだけ。</summary>
public sealed class FakeAiSettingsStore : IAiSettingsStore
{
    /// <summary>Load が返す設定。テストが差し替える。</summary>
    public AiSettings Settings { get; set; } = AiSettings.Default();

    public int LoadCalls { get; private set; }

    /// <summary>Save に渡された設定を呼ばれた順に。</summary>
    public List<AiSettings> SaveCalls { get; } = new();

    public AiSettings Load()
    {
        LoadCalls++;
        return Settings;
    }

    public void Save(AiSettings settings) => SaveCalls.Add(settings);
}
