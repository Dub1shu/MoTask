using System.Windows.Input;

namespace MoTask.App;

/// <summary>仕分けの 4 操作。キーから引く。</summary>
public enum TriageKeyAction
{
    Register,
    Merge,
    Reject,
    Postpone,
}

/// <summary>仕様 §6 のキー割当（T 登録 / E 統合 / X 却下 / L あとで）。修飾キー付きは対象外。</summary>
public static class TriageKeyMap
{
    public static TriageKeyAction? Resolve(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None) return null;
        return key switch
        {
            Key.T => TriageKeyAction.Register,
            Key.E => TriageKeyAction.Merge,
            Key.X => TriageKeyAction.Reject,
            Key.L => TriageKeyAction.Postpone,
            _ => null,
        };
    }
}
