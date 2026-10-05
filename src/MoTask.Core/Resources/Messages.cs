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
    public static string ProjectNameDuplicate => Get(nameof(ProjectNameDuplicate));
    public static string ProjectNameArchivedDuplicate => Get(nameof(ProjectNameArchivedDuplicate));
    public static string LabelNameDuplicate => Get(nameof(LabelNameDuplicate));
    public static string LabelNameArchivedDuplicate => Get(nameof(LabelNameArchivedDuplicate));
    public static string LabelColorInvalid => Get(nameof(LabelColorInvalid));
    public static string ProjectColorInvalid => Get(nameof(ProjectColorInvalid));
    public static string WipExceededFormat => Get(nameof(WipExceededFormat));
    public static string SaveFailed => Get(nameof(SaveFailed));
    public static string DefaultBoardName => Get(nameof(DefaultBoardName));
    public static string DefaultColumnBacklog => Get(nameof(DefaultColumnBacklog));
    public static string DefaultColumnToday => Get(nameof(DefaultColumnToday));
    public static string DefaultColumnActive => Get(nameof(DefaultColumnActive));
    public static string DefaultColumnReview => Get(nameof(DefaultColumnReview));
    public static string DefaultColumnDone => Get(nameof(DefaultColumnDone));
    public static string ClaudeNotFound => Get(nameof(ClaudeNotFound));
    public static string WorkingDirectoryMissingFormat => Get(nameof(WorkingDirectoryMissingFormat));
    public static string DefaultWorkingDirectoryFailedFormat => Get(nameof(DefaultWorkingDirectoryFailedFormat));
    public static string TaskAlreadyHasActiveJob => Get(nameof(TaskAlreadyHasActiveJob));
    public static string TaskDeletedCannotRunAi => Get(nameof(TaskDeletedCannotRunAi));
    public static string InstructionRequired => Get(nameof(InstructionRequired));
    public static string AiJobNotFound => Get(nameof(AiJobNotFound));
    public static string AiJobAlreadyFinished => Get(nameof(AiJobAlreadyFinished));
    public static string AiJobFolderMissing => Get(nameof(AiJobFolderMissing));
    public static string NoReviewColumn => Get(nameof(NoReviewColumn));
    public static string HooksExecutableNotFound => Get(nameof(HooksExecutableNotFound));
    public static string McpExecutableNotFound => Get(nameof(McpExecutableNotFound));
    public static string JobFolderFailedFormat => Get(nameof(JobFolderFailedFormat));
    public static string TerminalLaunchFailedFormat => Get(nameof(TerminalLaunchFailedFormat));
    public static string TerminalStartPromptFormat => Get(nameof(TerminalStartPromptFormat));
    public static string PlanningStartPromptFormat => Get(nameof(PlanningStartPromptFormat));
    public static string EventsFileGoneFormat => Get(nameof(EventsFileGoneFormat));
    public static string EventsWatchFailedFormat => Get(nameof(EventsWatchFailedFormat));

    // ---- MCP ブリッジ（MoTask.Mcp）----
    public static string McpAppNotFoundFormat => Get(nameof(McpAppNotFoundFormat));
    public static string McpAppStartTimeout => Get(nameof(McpAppStartTimeout));
    public static string McpUnauthorized => Get(nameof(McpUnauthorized));
    public static string McpUnexpectedStatusFormat => Get(nameof(McpUnexpectedStatusFormat));
    public static string McpConnectionLost => Get(nameof(McpConnectionLost));
    public static string McpUnexpectedFailure => Get(nameof(McpUnexpectedFailure));
    public static string PlanningInstructionDefault => Get(nameof(PlanningInstructionDefault));
    public static string PlanningInstructionContractFormat => Get(nameof(PlanningInstructionContractFormat));
    public static string PlanningRunAlreadyRunning => Get(nameof(PlanningRunAlreadyRunning));
    public static string PlanningRunNotFound => Get(nameof(PlanningRunNotFound));
    public static string PlanningRunAlreadyFinished => Get(nameof(PlanningRunAlreadyFinished));
    public static string PlanNotSubmitted => Get(nameof(PlanNotSubmitted));
    public static string PlanningCompleteMissing => Get(nameof(PlanningCompleteMissing));
    public static string PlanningCompleteWithoutPlan => Get(nameof(PlanningCompleteWithoutPlan));
    public static string CandidateFieldRequiredFormat => Get(nameof(CandidateFieldRequiredFormat));
    public static string CandidateEvidenceRequired => Get(nameof(CandidateEvidenceRequired));
    public static string CandidateActionInvalid => Get(nameof(CandidateActionInvalid));
    public static string CandidateMergeTargetMissing => Get(nameof(CandidateMergeTargetMissing));
    public static string CandidateLabelUnknown => Get(nameof(CandidateLabelUnknown));
    public static string PlanNotAnObject => Get(nameof(PlanNotAnObject));
    public static string PlanGroupsInvalid => Get(nameof(PlanGroupsInvalid));
    public static string PlanGroupNotAnObjectFormat => Get(nameof(PlanGroupNotAnObjectFormat));
    public static string PlanGroupKeyInvalidFormat => Get(nameof(PlanGroupKeyInvalidFormat));
    public static string PlanItemsInvalidFormat => Get(nameof(PlanItemsInvalidFormat));
    public static string PlanItemNeedsIdFormat => Get(nameof(PlanItemNeedsIdFormat));
    public static string PlanFirstThingNeedsId => Get(nameof(PlanFirstThingNeedsId));
    public static string PlanningRunNotRunningFormat => Get(nameof(PlanningRunNotRunningFormat));
    public static string CandidateAlreadyDecidedElsewhere => Get(nameof(CandidateAlreadyDecidedElsewhere));
    public static string CandidateAlreadyInThisRun => Get(nameof(CandidateAlreadyInThisRun));
    public static string PlanningTerminalClosed => Get(nameof(PlanningTerminalClosed));
    public static string CandidateNotFound => Get(nameof(CandidateNotFound));
    public static string CandidateAlreadyDecided => Get(nameof(CandidateAlreadyDecided));
    public static string CandidateNoteHeaderFormat => Get(nameof(CandidateNoteHeaderFormat));
    public static string CandidateNoteFromFormat => Get(nameof(CandidateNoteFromFormat));
    public static string BulkSkippedFormat => Get(nameof(BulkSkippedFormat));
    public static string MergeTargetMissing => Get(nameof(MergeTargetMissing));
}
