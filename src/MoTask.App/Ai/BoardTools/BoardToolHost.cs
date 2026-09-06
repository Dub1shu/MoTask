using System.Globalization;
using System.Text.Json;
using MoTask.App.Resources;
using MoTask.Core;
using MoTask.Core.Abstractions;
using MoTask.Core.Filtering;
using MoTask.Core.Model;
using MoTask.Core.Services;

namespace MoTask.App.Ai.BoardTools;

/// <summary>
/// MCP の board ツール（仕様 §6）。ボード操作の知識をここに閉じ込め、HTTP も JSON-RPC も知らない。
/// 責務は 4 つ: ツール定義、引数の名前↔id 解決と既定値の補完、IBoardService の呼び出し、結果の JSON 整形。
/// </summary>
public sealed class BoardToolHost : IBoardChangeSource
{
    public const string GetBoard = "get_board";
    public const string ListTasks = "list_tasks";
    public const string GetTask = "get_task";
    public const string AddTask = "add_task";
    public const string UpdateTask = "update_task";
    public const string MoveTask = "move_task";

    private readonly IBoardService _service;
    private readonly IClock _clock;
    private readonly IReadOnlyList<McpTool> _tools;

    public BoardToolHost(IBoardService service, IClock clock)
    {
        _service = service;
        _clock = clock;
        _tools = BuildTools();
    }

    public IReadOnlyList<McpTool> Tools => _tools;

    // ---------- ツール定義 ----------

    private IReadOnlyList<McpTool> BuildTools() => new[]
    {
        new McpTool(GetBoard,
            "MoTask のボード構成（列・プロジェクト・ラベル）を返す。列の role が Done の列へ move_task すると完了になる。",
            new { type = "object", properties = new { } },
            GetBoardAsync),

        new McpTool(ListTasks,
            "タスクを一覧する。列ごとにまとめて返る。本文は 200 字で切り詰めるので、全文は get_task で取る。",
            new
            {
                type = "object",
                properties = new
                {
                    column = IdOrName("列。省略すると全列。"),
                    project = IdOrName("プロジェクト。省略すると全プロジェクト。"),
                    label = new
                    {
                        description = "ラベル。id か名前、または その配列（配列は AND ＝すべて付いているものだけ）。",
                    },
                    due = new
                    {
                        type = "string",
                        description = "期日の絞り込み。",
                        @enum = new[] { "all", "today", "this_week", "overdue" },
                    },
                    search = new { type = "string", description = "タイトルと本文の部分一致。" },
                },
            },
            ListTasksAsync),

        new McpTool(GetTask,
            "タスク 1 件の詳細（本文の全文と作成・更新日時）を返す。",
            new
            {
                type = "object",
                properties = new { task = new { type = "integer", description = "タスクの id。" } },
                required = new[] { "task" },
            },
            GetTaskAsync),

        new McpTool(AddTask,
            "タスクを追加する。column を省略すると先頭の Active ロールの列に入る。",
            new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "タイトル（必須）。" },
                    column = IdOrName("入れる列。省略すると先頭の Active ロールの列。"),
                    description = new { type = "string", description = "本文。" },
                    project = IdOrName("プロジェクト。"),
                    labels = new { description = "ラベル。id か名前の配列。" },
                    due = new { type = "string", description = "期日。YYYY-MM-DD。" },
                },
                required = new[] { "title" },
            },
            AddTaskAsync),

        new McpTool(UpdateTask,
            "タスクを更新する。渡した項目だけを変え、省略した項目は現在値のまま。project と due は null を明示すると外れる。",
            new
            {
                type = "object",
                properties = new
                {
                    task = new { type = "integer", description = "タスクの id。" },
                    title = new { type = "string" },
                    description = new { type = "string" },
                    project = IdOrName("プロジェクト。null で外す。"),
                    due = new { type = "string", description = "期日。YYYY-MM-DD。null で外す。" },
                    labels = new { description = "ラベル。id か名前の配列。空配列で全部外す。" },
                },
                required = new[] { "task" },
            },
            UpdateTaskAsync),

        new McpTool(MoveTask,
            "タスクを別の列へ移す。get_board の role が Done の列へ移すと完了になる。",
            new
            {
                type = "object",
                properties = new
                {
                    task = new { type = "integer", description = "タスクの id。" },
                    column = IdOrName("移動先の列（必須）。"),
                    position = new { type = "integer", description = "移動先の列での位置（0 始まり）。省略すると末尾。" },
                },
                required = new[] { "task", "column" },
            },
            MoveTaskAsync),
    };

    private static object IdOrName(string description) => new
    {
        description = description + " id（整数）でも名前（文字列）でもよい。",
        oneOf = new object[] { new { type = "integer" }, new { type = "string" } },
    };

    // ---------- 参照系 ----------

    private Task<McpToolResult> GetBoardAsync(JsonElement arguments, CancellationToken ct)
        => WithBoardAsync(ctx => Ok(BoardJson.Serialize(
            BoardJson.BoardShape(ctx.Board, ctx.Projects, ctx.Labels), Array.Empty<string>())), ct);

    private Task<McpToolResult> ListTasksAsync(JsonElement arguments, CancellationToken ct)
        => WithBoardAsync(ctx =>
        {
            var args = new BoardArgs(arguments);
            if (!args.TryRef("column", out var columnRef)) return Invalid(Strings.McpKindColumn);
            if (!args.TryRef("project", out var projectRef)) return Invalid(Strings.McpKindProject);
            if (!args.TryRefs("label", out var labelRefs)) return Invalid(Strings.McpKindLabel);

            Column? only = null;
            if (columnRef is { } cr)
            {
                var found = ctx.ResolveColumn(cr);
                if (!found.IsSuccess) return Error(found.Error!);
                only = found.Value;
            }

            int? projectId = null;
            if (projectRef is { } pr)
            {
                var found = ctx.ResolveProject(pr);
                if (!found.IsSuccess) return Error(found.Error!);
                projectId = found.Value!.Id;
            }

            var labelIds = new HashSet<int>();
            foreach (var reference in labelRefs ?? Array.Empty<McpRef>())
            {
                var found = ctx.ResolveLabel(reference);
                if (!found.IsSuccess) return Error(found.Error!);
                labelIds.Add(found.Value!.Id);
            }

            var rawDue = args.String("due");
            if (!TryDueFilter(rawDue, out var due))
            {
                return Error(string.Format(CultureInfo.CurrentCulture, Strings.McpDueFilterInvalidFormat, rawDue));
            }

            // ShowDeleted は常に false（仕様 §6.2）
            var filter = new TaskFilter(projectId, labelIds.Count > 0 ? labelIds : null, due, args.String("search") ?? "");
            var columns = (only is null ? ctx.Columns : new[] { only }).Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["tasks"] = filter.Apply(c.Tasks.OrderBy(t => t.Position), _clock.Today)
                    .Select(t => BoardJson.TaskSummary(t, c, ctx.ProjectNames)).ToArray(),
            }).ToArray();

            return Ok(BoardJson.Serialize(new Dictionary<string, object?> { ["columns"] = columns }, Array.Empty<string>()));
        }, ct);

    private Task<McpToolResult> GetTaskAsync(JsonElement arguments, CancellationToken ct)
        => WithBoardAsync(ctx =>
        {
            var args = new BoardArgs(arguments);
            if (!TryTaskId(args, out var taskId, out var taskError)) return Error(taskError);
            if (ctx.FindTask(taskId) is not { } found) return Error(Messages.TaskNotFound);
            return Ok(BoardJson.Serialize(
                BoardJson.TaskDetail(found.Task, found.Column, ctx.ProjectNames), Array.Empty<string>()));
        }, ct);

    /// <summary>MCP 経由の書き込みが成功するたびに上がる。購読側で UI スレッドへ載せ替えること。</summary>
    public event EventHandler? BoardChanged;

    // ---------- 書き込み系 ----------

    private async Task<McpToolResult> AddTaskAsync(JsonElement arguments, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Error(loaded.Error!);
        var ctx = loaded.Value!;
        var args = new BoardArgs(arguments);

        var title = (args.String("title") ?? "").Trim();
        if (title.Length == 0) return Error(Messages.TitleRequired);

        if (!args.TryRef("column", out var columnRef)) return Invalid(Strings.McpKindColumn);
        if (!args.TryRef("project", out var projectRef)) return Invalid(Strings.McpKindProject);
        if (!args.TryRefs("labels", out var labelRefs)) return Invalid(Strings.McpKindLabel);
        if (!args.TryDate("due", out var due))
        {
            return Error(string.Format(CultureInfo.CurrentCulture, Strings.McpDueInvalidFormat, args.String("due")));
        }

        Column column;
        if (columnRef is { } cr)
        {
            var found = ctx.ResolveColumn(cr);
            if (!found.IsSuccess) return Error(found.Error!);
            column = found.Value!;
        }
        else
        {
            // アプリの N キーは「選択中の列、なければ先頭」。MCP に「選択中」が無いので Active を優先する（仕様 §6.4）。
            var fallback = ctx.Columns.FirstOrDefault(c => c.Role == ColumnRole.Active) ?? ctx.Columns.FirstOrDefault();
            if (fallback is null) return Error(Strings.McpBoardHasNoColumn);
            column = fallback;
        }

        int? projectId = null;
        if (projectRef is { } pr)
        {
            var found = ctx.ResolveProject(pr);
            if (!found.IsSuccess) return Error(found.Error!);
            projectId = found.Value!.Id;
        }

        var labelIds = new List<int>();
        foreach (var reference in labelRefs ?? Array.Empty<McpRef>())
        {
            var found = ctx.ResolveLabel(reference);
            if (!found.IsSuccess) return Error(found.Error!);
            labelIds.Add(found.Value!.Id);
        }

        var created = await _service.CreateTaskAsync(column.Id, title, ct).ConfigureAwait(false);
        if (!created.IsSuccess) return Error(created.Error!);

        // ここから先で失敗してもタスクは既に出来ているので、どの経路でも画面を更新させる。
        try
        {
            var warnings = created.Warnings.ToList();
            var taskId = created.Value!.Id;
            var description = args.String("description") ?? "";

            if (description.Length > 0 || projectId is not null || due is not null)
            {
                var updated = await _service
                    .UpdateTaskAsync(new TaskUpdate(taskId, title, description, projectId, due), ct)
                    .ConfigureAwait(false);
                if (!updated.IsSuccess) return Partial(taskId, updated.Error!);
                warnings.AddRange(updated.Warnings);
            }

            if (labelIds.Count > 0)
            {
                var labelled = await _service.SetTaskLabelsAsync(taskId, labelIds, ct).ConfigureAwait(false);
                if (!labelled.IsSuccess) return Partial(taskId, labelled.Error!);
                warnings.AddRange(labelled.Warnings);
            }

            return await ReadBackAsync(taskId, warnings, ct).ConfigureAwait(false);
        }
        finally
        {
            RaiseChanged();
        }
    }

    private async Task<McpToolResult> UpdateTaskAsync(JsonElement arguments, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Error(loaded.Error!);
        var ctx = loaded.Value!;
        var args = new BoardArgs(arguments);

        if (!TryTaskId(args, out var taskId, out var taskError)) return Error(taskError);
        if (ctx.FindTask(taskId) is not { } found) return Error(Messages.TaskNotFound);
        var task = found.Task;

        if (!args.TryRef("project", out var projectRef)) return Invalid(Strings.McpKindProject);
        if (!args.TryRefs("labels", out var labelRefs)) return Invalid(Strings.McpKindLabel);

        // 省略は現在値の保持、明示的な null は「外す」（仕様 §6.5）
        var title = args.Has("title") ? (args.String("title") ?? "") : task.Title;
        var description = args.Has("description") ? (args.String("description") ?? "") : task.Description;

        var projectId = task.ProjectId;
        if (args.IsExplicitNull("project")) projectId = null;
        else if (projectRef is { } pr)
        {
            var resolved = ctx.ResolveProject(pr);
            if (!resolved.IsSuccess) return Error(resolved.Error!);
            projectId = resolved.Value!.Id;
        }

        var due = task.DueDate;
        if (args.IsExplicitNull("due")) due = null;
        else if (args.Has("due"))
        {
            if (!args.TryDate("due", out var parsed))
            {
                return Error(string.Format(CultureInfo.CurrentCulture, Strings.McpDueInvalidFormat, args.String("due")));
            }
            due = parsed;
        }

        List<int>? labelIds = null;
        if (args.Has("labels") && labelRefs is not null)
        {
            labelIds = new List<int>();
            foreach (var reference in labelRefs)
            {
                var resolved = ctx.ResolveLabel(reference);
                if (!resolved.IsSuccess) return Error(resolved.Error!);
                labelIds.Add(resolved.Value!.Id);
            }
        }

        var updated = await _service
            .UpdateTaskAsync(new TaskUpdate(taskId, title, description, projectId, due), ct)
            .ConfigureAwait(false);
        if (!updated.IsSuccess) return Error(updated.Error!);

        try
        {
            var warnings = updated.Warnings.ToList();
            if (labelIds is not null)
            {
                var labelled = await _service.SetTaskLabelsAsync(taskId, labelIds, ct).ConfigureAwait(false);
                if (!labelled.IsSuccess) return Error(labelled.Error!);
                warnings.AddRange(labelled.Warnings);
            }
            return await ReadBackAsync(taskId, warnings, ct).ConfigureAwait(false);
        }
        finally
        {
            RaiseChanged();
        }
    }

    private async Task<McpToolResult> MoveTaskAsync(JsonElement arguments, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Error(loaded.Error!);
        var ctx = loaded.Value!;
        var args = new BoardArgs(arguments);

        if (!TryTaskId(args, out var taskId, out var taskError)) return Error(taskError);
        if (!args.TryRef("column", out var columnRef)) return Invalid(Strings.McpKindColumn);
        if (columnRef is not { } reference) return Error(Strings.McpColumnRequired);
        if (ctx.FindTask(taskId) is null) return Error(Messages.TaskNotFound);

        var target = ctx.ResolveColumn(reference);
        if (!target.IsSuccess) return Error(target.Error!);

        // 省略時は末尾。MoveTaskAsync が範囲外を端へ丸めるので、大きい値を渡せばよい。
        var position = args.Int("position") ?? int.MaxValue;
        var moved = await _service.MoveTaskAsync(taskId, target.Value!.Id, position, ct).ConfigureAwait(false);
        if (!moved.IsSuccess) return Error(moved.Error!);

        try
        {
            return await ReadBackAsync(taskId, moved.Warnings, ct).ConfigureAwait(false);
        }
        finally
        {
            RaiseChanged();
        }
    }

    /// <summary>書き込み後の姿を読み直して返す（返すのは get_task と同じ形。仕様 §6.4）。</summary>
    private async Task<McpToolResult> ReadBackAsync(int taskId, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Error(loaded.Error!);
        var ctx = loaded.Value!;
        if (ctx.FindTask(taskId) is not { } found) return Error(Messages.TaskNotFound);
        return Ok(BoardJson.Serialize(BoardJson.TaskDetail(found.Task, found.Column, ctx.ProjectNames), warnings));
    }

    private void RaiseChanged() => BoardChanged?.Invoke(this, EventArgs.Empty);

    // ---------- 共通 ----------

    /// <summary>ボード・プロジェクト・ラベルを読んでから本体を呼ぶ。照会に失敗したらツールエラー。</summary>
    private async Task<McpToolResult> WithBoardAsync(Func<BoardContext, McpToolResult> body, CancellationToken ct)
    {
        var context = await LoadAsync(ct).ConfigureAwait(false);
        return context.IsSuccess ? body(context.Value!) : McpToolResult.Error(context.Error!);
    }

    private async Task<Result<BoardContext>> LoadAsync(CancellationToken ct)
    {
        var board = await _service.GetBoardAsync(ct).ConfigureAwait(false);
        if (!board.IsSuccess) return Result.Fail<BoardContext>(board.Error!);
        var projects = await _service.GetProjectsAsync(ct).ConfigureAwait(false);
        var labels = await _service.GetLabelsAsync(ct).ConfigureAwait(false);
        return Result.Ok(new BoardContext(board.Value!, projects, labels));
    }

    private static McpToolResult Ok(string json) => McpToolResult.Ok(json);

    private static McpToolResult Error(string message) => McpToolResult.Error(message);

    private static McpToolResult Invalid(string kind)
        => McpToolResult.Error(string.Format(CultureInfo.CurrentCulture, Strings.McpRefInvalidFormat, kind));

    /// <summary>
    /// add_task が「作成には成功したが、続く更新で失敗した」とき。失敗なので isError は立てたまま、
    /// 作成済みのタスク id を本文に明示する。これが無いとモデルは作成ごと失敗したと読んで再試行し、
    /// タイトルだけのタスクが重複して増える。
    /// </summary>
    private static McpToolResult Partial(int taskId, string error)
        => McpToolResult.Error(string.Format(
            CultureInfo.CurrentCulture, Strings.McpAddTaskPartialFormat, taskId, error));

    /// <summary>
    /// task 引数を読む。キー自体が無ければ「必須です」、あるのに整数として読めなければ
    /// 「整数で指定してください」。両者を言い分けないとモデルが自力で直せない。
    /// </summary>
    private static bool TryTaskId(BoardArgs args, out int taskId, out string error)
    {
        if (args.Int("task") is int id)
        {
            taskId = id;
            error = "";
            return true;
        }

        taskId = 0;
        error = args.Has("task") ? Strings.McpTaskNotAnInteger : Strings.McpTaskRequired;
        return false;
    }

    private static bool TryDueFilter(string? value, out DueFilter due)
    {
        switch (value)
        {
            case null or "" or "all": due = DueFilter.All; return true;
            case "today": due = DueFilter.Today; return true;
            case "this_week": due = DueFilter.ThisWeek; return true;
            case "overdue": due = DueFilter.Overdue; return true;
            default: due = DueFilter.All; return false;
        }
    }

    /// <summary>1 回のツール呼び出しの間だけ生きるボードの読み取り結果。</summary>
    private sealed class BoardContext
    {
        public BoardContext(Board board, IReadOnlyList<Project> projects, IReadOnlyList<Label> labels)
        {
            Board = board;
            Projects = projects;
            Labels = labels;
            Columns = board.Columns.OrderBy(c => c.Order).ToList();
            ProjectNames = projects.ToDictionary(p => p.Id, p => p.Name);
        }

        public Board Board { get; }
        public IReadOnlyList<Project> Projects { get; }
        public IReadOnlyList<Label> Labels { get; }
        public IReadOnlyList<Column> Columns { get; }
        public IReadOnlyDictionary<int, string> ProjectNames { get; }

        public Result<Column> ResolveColumn(McpRef reference)
            => BoardLookup.Resolve(Columns, reference, c => c.Id, c => c.Name, Strings.McpKindColumn);

        public Result<Project> ResolveProject(McpRef reference)
            => BoardLookup.Resolve(Projects, reference, p => p.Id, p => p.Name, Strings.McpKindProject);

        public Result<Label> ResolveLabel(McpRef reference)
            => BoardLookup.Resolve(Labels, reference, l => l.Id, l => l.Name, Strings.McpKindLabel);

        /// <summary>論理削除済みは見えない（仕様 §6「共通の約束」）。</summary>
        public (Column Column, TaskItem Task)? FindTask(int taskId)
        {
            foreach (var column in Columns)
            {
                foreach (var task in column.Tasks)
                {
                    if (task.Id == taskId && !task.IsDeleted) return (column, task);
                }
            }
            return null;
        }
    }
}
