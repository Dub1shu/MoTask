using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Tests.Fakes;

// FakeBoardService が積む呼び出しの記録。CancellationToken は記録しない
// （テストが Arg.Any<CancellationToken>() で読み飛ばしていたものなので、比べる意味がない）。

public sealed record CreateTaskCall(int ColumnId, string Title);
public sealed record UpdateTaskCall(TaskUpdate Update);
public sealed record MoveTaskCall(int TaskId, int ToColumnId, int Position);

// IReadOnlyCollection<int> は record の既定 EqualityComparer<T>.Default では参照比較になり、
// 中身が同じでも .Should().Be(...) は必ず失敗する。TaskId と LabelIds は別々に検証すること。
public sealed record SetTaskLabelsCall(int TaskId, IReadOnlyCollection<int> LabelIds);

public sealed record AddColumnCall(string Name, ColumnRole Role);
public sealed record RenameColumnCall(int ColumnId, string Name);
public sealed record SetColumnRoleCall(int ColumnId, ColumnRole Role);

// IReadOnlyList<int> は record の既定 EqualityComparer<T>.Default では参照比較になり、
// 中身が同じでも .Should().Be(...) は必ず失敗する。OrderedColumnIds は別途検証すること。
public sealed record ReorderColumnsCall(IReadOnlyList<int> OrderedColumnIds);

public sealed record SetWipLimitCall(int ColumnId, int? WipLimit);
public sealed record CreateLabelCall(string Name, string Color);

public sealed record RenameCall(int Id, string Name);

public sealed record SetLabelColorCall(int LabelId, string Color);

public sealed record SetProjectColorCall(int ProjectId, string? Color);
public sealed record SetProjectWorkingDirectoryCall(int ProjectId, string? Path);
public sealed record GetHistoryCall(int TaskId);
public sealed record ReorderClassificationsCall(IReadOnlyList<int> OrderedIds);
