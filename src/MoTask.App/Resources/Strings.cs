using System.Globalization;
using System.Resources;

namespace MoTask.App.Resources;

public static class Strings
{
    private static readonly ResourceManager Rm =
        new("MoTask.App.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string AppTitle => Get(nameof(AppTitle));
    public static string Brand => Get(nameof(Brand));
    public static string ViewBoard => Get(nameof(ViewBoard));
    public static string FilterAllProjects => Get(nameof(FilterAllProjects));
    public static string DueAll => Get(nameof(DueAll));
    public static string DueToday => Get(nameof(DueToday));
    public static string DueThisWeek => Get(nameof(DueThisWeek));
    public static string DueOverdue => Get(nameof(DueOverdue));
    public static string Search => Get(nameof(Search));
    public static string ShowDeleted => Get(nameof(ShowDeleted));
    public static string NewTask => Get(nameof(NewTask));
    public static string AddColumn => Get(nameof(AddColumn));
    public static string AddTaskInline => Get(nameof(AddTaskInline));
    public static string Rename => Get(nameof(Rename));
    public static string ChangeRole => Get(nameof(ChangeRole));
    public static string SetWip => Get(nameof(SetWip));
    public static string ClearWip => Get(nameof(ClearWip));
    public static string DeleteColumn => Get(nameof(DeleteColumn));
    public static string RoleBacklog => Get(nameof(RoleBacklog));
    public static string RoleActive => Get(nameof(RoleActive));
    public static string RoleReview => Get(nameof(RoleReview));
    public static string RoleDone => Get(nameof(RoleDone));
    public static string Delete => Get(nameof(Delete));
    public static string Restore => Get(nameof(Restore));
    public static string Close => Get(nameof(Close));
    public static string Deleted => Get(nameof(Deleted));
    public static string Description => Get(nameof(Description));
    public static string Project => Get(nameof(Project));
    public static string Labels => Get(nameof(Labels));
    public static string DueDate => Get(nameof(DueDate));
    public static string Column => Get(nameof(Column));
    public static string History => Get(nameof(History));
    public static string NoProject => Get(nameof(NoProject));
    public static string NewProjectHint => Get(nameof(NewProjectHint));
    public static string NewLabelHint => Get(nameof(NewLabelHint));
    public static string HistoryCreatedFormat => Get(nameof(HistoryCreatedFormat));
    public static string HistoryMovedFormat => Get(nameof(HistoryMovedFormat));
    public static string HistoryEditedFormat => Get(nameof(HistoryEditedFormat));
    public static string HistoryDeleted => Get(nameof(HistoryDeleted));
    public static string HistoryRestored => Get(nameof(HistoryRestored));
    public static string HistoryUnknown => Get(nameof(HistoryUnknown));
    public static string HistoryTimestampFormat => Get(nameof(HistoryTimestampFormat));
    public static string HistoryFieldJoiner => Get(nameof(HistoryFieldJoiner));
    public static string FieldTitle => Get(nameof(FieldTitle));
    public static string FieldDescription => Get(nameof(FieldDescription));
    public static string FieldProject => Get(nameof(FieldProject));
    public static string FieldDueDate => Get(nameof(FieldDueDate));
    public static string FieldLabels => Get(nameof(FieldLabels));
    public static string FieldUnknown => Get(nameof(FieldUnknown));
    public static string CardDueFormat => Get(nameof(CardDueFormat));
    public static string ColumnCountFormat => Get(nameof(ColumnCountFormat));
    public static string ColumnCountWithLimitFormat => Get(nameof(ColumnCountWithLimitFormat));
    public static string UnknownColumn => Get(nameof(UnknownColumn));
    public static string DbOpenFailedFormat => Get(nameof(DbOpenFailedFormat));
    public static string DbRecreatedFormat => Get(nameof(DbRecreatedFormat));
    public static string DbRecreateFailedFormat => Get(nameof(DbRecreateFailedFormat));
}
