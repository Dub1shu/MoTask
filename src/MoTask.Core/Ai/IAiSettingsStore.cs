namespace MoTask.Core.Ai;

public interface IAiSettingsStore
{
    /// <summary>無ければ AiSettings.Default()。壊れていても既定で返す（起動を止めない）。</summary>
    AiSettings Load();
    void Save(AiSettings settings);
}
