using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoTask.App.Resources;
using MoTask.Core.Planning;

namespace MoTask.App.ViewModels;

/// <summary>
/// 左パネル・状態 2「最初にやる1件」（仕様 §6、ワイヤー 4b）。計画の JSON にあるのは taskId / externalId と
/// reason だけなので、出すのもタイトル・選定理由・「ボードで開く」の 3 つだけ（仕様 §3）。
/// </summary>
public sealed partial class FirstThingViewModel : ObservableObject
{
    private readonly Action<int> _openOnBoard;

    public FirstThingViewModel(Action<int> openOnBoard)
    {
        _openOnBoard = openOnBoard;
    }

    public string Heading => Strings.PlanFirstThingHeading;
    public string EmptyText => Strings.PlanNoFirstThing;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _reason = "";
    /// <summary>firstThing が解決できず today の先頭に繰り下げた（仕様 §4）。見出しに「（暫定）」を付ける。</summary>
    [ObservableProperty] private bool _isFallback;
    [ObservableProperty] private bool _hasFirstThing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenOnBoard))]
    [NotifyCanExecuteChangedFor(nameof(OpenOnBoardCommand))]
    private int? _taskId;

    /// <summary>候補行（仕分け待ち）が最初の 1 件になっている間はボードに無いので開けない。</summary>
    public bool CanOpenOnBoard => TaskId is not null;

    public void Update(ResolvedPlan plan)
    {
        HasFirstThing = plan.FirstThing is not null;
        Title = plan.FirstThing?.Title ?? "";
        Reason = plan.FirstThingReason;
        IsFallback = plan.FirstThingIsFallback && plan.FirstThing is not null;
        TaskId = (plan.FirstThing as TaskRow)?.TaskId;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOnBoard))]
    private void OpenOnBoard()
    {
        if (TaskId is int id) _openOnBoard(id);
    }
}
