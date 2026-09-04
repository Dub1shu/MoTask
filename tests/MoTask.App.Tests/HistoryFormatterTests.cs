using FluentAssertions;
using MoTask.App.Resources;
using MoTask.App.ViewModels;
using MoTask.Core.Model;
using Xunit;

namespace MoTask.App.Tests;

public class HistoryFormatterTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    private static string Name(int id) => id switch { 1 => "未着手", 2 => "進行中", _ => "?" };

    [Fact]
    public void Moved_ShowsLocalTimeAndArrow()
    {
        var entry = new HistoryEntry
        {
            At = new DateTime(2026, 9, 3, 23, 40, 0, DateTimeKind.Utc), Kind = HistoryKind.Moved,
            FromColumnId = 1, ToColumnId = 2,
        };
        HistoryFormatter.Format(entry, Name, Tokyo).Should().Be("9/4 8:40 未着手 → 進行中");
    }

    [Fact]
    public void Created_Edited_Deleted_Restored()
    {
        var at = new DateTime(2026, 9, 4, 0, 5, 0, DateTimeKind.Utc);
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Created, ToColumnId = 1 }, Name, Tokyo)
            .Should().Be("9/4 9:05 未着手 に作成");
        var detail = HistoryDetail.Serialize(new Dictionary<string, FieldChange>
        {
            ["Title"] = new("a", "b"), ["DueDate"] = new(null, "2026-09-10"),
        });
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Edited, Detail = detail }, Name, Tokyo)
            .Should().Be("9/4 9:05 タイトル、期限 を変更");
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Deleted }, Name, Tokyo)
            .Should().Be("9/4 9:05 削除");
        HistoryFormatter.Format(new HistoryEntry { At = at, Kind = HistoryKind.Restored }, Name, Tokyo)
            .Should().Be("9/4 9:05 復元");
    }

    /// <summary>
    /// F1: HistoryDetail.Deserialize は不正な JSON で JsonException を投げる。壊れた履歴行1件のために
    /// 例外を漏らして呼び出し元（LoadHistoryAsync 経由の PendingSave）を fault させてはいけない。
    /// </summary>
    [Fact]
    public void Edited_WithCorruptDetail_FallsBackToUnknownWithoutThrowing()
    {
        var at = new DateTime(2026, 9, 4, 0, 5, 0, DateTimeKind.Utc);
        var entry = new HistoryEntry { At = at, Kind = HistoryKind.Edited, Detail = "{not valid json" };

        var act = () => HistoryFormatter.Format(entry, Name, Tokyo);

        act.Should().NotThrow();
        act().Should().Be($"9/4 9:05 {Strings.HistoryUnknown}");
    }

    /// <summary>
    /// F2: 未知のフィールドキー（将来のスキーマ変更や壊れたデータ）は、生の JSON キーではなく
    /// resx 由来の文言にフォールバックする。日本語のみ UI に英語リテラルを出さない。
    /// </summary>
    [Fact]
    public void FieldName_UnknownKey_FallsBackToResxStringNotRawKey()
    {
        HistoryFormatter.FieldName("SomeFutureField").Should().Be(Strings.FieldUnknown);
        HistoryFormatter.FieldName("SomeFutureField").Should().NotBe("SomeFutureField");
    }

    [Fact]
    public void Edited_WithUnknownFieldKey_UsesResxFallbackInSentence()
    {
        var at = new DateTime(2026, 9, 4, 0, 5, 0, DateTimeKind.Utc);
        var detail = HistoryDetail.Serialize(new Dictionary<string, FieldChange> { ["Assignee"] = new(null, "x") });
        var entry = new HistoryEntry { At = at, Kind = HistoryKind.Edited, Detail = detail };

        HistoryFormatter.Format(entry, Name, Tokyo).Should().Be($"9/4 9:05 {Strings.FieldUnknown} を変更");
    }
}
