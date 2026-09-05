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
    public static string DefaultBoardName => Get(nameof(DefaultBoardName));
    public static string DefaultColumnBacklog => Get(nameof(DefaultColumnBacklog));
    public static string DefaultColumnActive => Get(nameof(DefaultColumnActive));
    public static string DefaultColumnReview => Get(nameof(DefaultColumnReview));
    public static string DefaultColumnDone => Get(nameof(DefaultColumnDone));
    public static string ClaudeNotFound => Get(nameof(ClaudeNotFound));
    public static string WorkingDirectoryMissingFormat => Get(nameof(WorkingDirectoryMissingFormat));
    public static string DefaultWorkingDirectoryFailedFormat => Get(nameof(DefaultWorkingDirectoryFailedFormat));
    public static string ConcurrencyLimitFormat => Get(nameof(ConcurrencyLimitFormat));
    public static string TaskAlreadyHasActiveJob => Get(nameof(TaskAlreadyHasActiveJob));
    public static string TaskDeletedCannotRunAi => Get(nameof(TaskDeletedCannotRunAi));
    public static string InstructionRequired => Get(nameof(InstructionRequired));
    public static string AiJobNotFound => Get(nameof(AiJobNotFound));
    public static string AiJobNotActive => Get(nameof(AiJobNotActive));
    public static string AiJobNotSuspended => Get(nameof(AiJobNotSuspended));
    public static string SuspendedByShutdown => Get(nameof(SuspendedByShutdown));
    public static string StoppedByUser => Get(nameof(StoppedByUser));
    public static string ResumeInstruction => Get(nameof(ResumeInstruction));
    public static string ResumeFailedFormat => Get(nameof(ResumeFailedFormat));
    public static string NoReviewColumn => Get(nameof(NoReviewColumn));
    public static string AgentExitedWithCodeFormat => Get(nameof(AgentExitedWithCodeFormat));
    public static string AgentFailedFormat => Get(nameof(AgentFailedFormat));
    public static string PermissionRuleNotFound => Get(nameof(PermissionRuleNotFound));
    public static string DeniedByRule => Get(nameof(DeniedByRule));
    public static string DeniedByHuman => Get(nameof(DeniedByHuman));
    public static string ApprovalUiFailed => Get(nameof(ApprovalUiFailed));
}
