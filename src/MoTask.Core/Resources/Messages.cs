using System.Globalization;
using System.Resources;

namespace MoTask.Core;

public static class Messages
{
    private static readonly ResourceManager Rm =
        new("MoTask.Core.Resources.Messages", typeof(Messages).Assembly);

    private static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string TitleRequired => Get(nameof(TitleRequired));
    public static string TaskNotFound => Get(nameof(TaskNotFound));
    public static string ColumnNotFound => Get(nameof(ColumnNotFound));
    public static string BoardNotFound => Get(nameof(BoardNotFound));
    public static string ProjectNotFound => Get(nameof(ProjectNotFound));
    public static string LabelNotFound => Get(nameof(LabelNotFound));
    public static string TaskAlreadyDeleted => Get(nameof(TaskAlreadyDeleted));
    public static string TaskNotDeleted => Get(nameof(TaskNotDeleted));
    public static string ColumnNameRequired => Get(nameof(ColumnNameRequired));
    public static string DoneColumnCannotChangeRole => Get(nameof(DoneColumnCannotChangeRole));
    public static string CannotAssignDoneRole => Get(nameof(CannotAssignDoneRole));
    public static string DoneColumnCannotBeDeleted => Get(nameof(DoneColumnCannotBeDeleted));
    public static string ColumnHasTasks => Get(nameof(ColumnHasTasks));
    public static string ReorderMustIncludeAllColumns => Get(nameof(ReorderMustIncludeAllColumns));
    public static string WipLimitMustBePositive => Get(nameof(WipLimitMustBePositive));
    public static string ProjectNameRequired => Get(nameof(ProjectNameRequired));
    public static string LabelNameRequired => Get(nameof(LabelNameRequired));
    public static string WipExceededFormat => Get(nameof(WipExceededFormat));
    public static string SaveFailed => Get(nameof(SaveFailed));
}
