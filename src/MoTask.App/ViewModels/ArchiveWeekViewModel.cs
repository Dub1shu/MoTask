using System.Globalization;
using MoTask.App.Resources;

namespace MoTask.App.ViewModels;

/// <summary>アーカイブの週 1 つ分。見出しは「9/7（月） 〜 9/13（日）」、行は完了日時の新しい順。</summary>
public sealed class ArchiveWeekViewModel
{
    public DateOnly Start { get; }
    public string Heading { get; }
    public string CountText { get; }
    public IReadOnlyList<ArchiveItemViewModel> Items { get; }

    public ArchiveWeekViewModel(DateOnly start, IReadOnlyList<ArchiveItemViewModel> items)
    {
        Start = start;
        Items = items;
        Heading = string.Format(CultureInfo.CurrentCulture, Strings.ArchiveWeekHeadingFormat,
            ArchiveItemViewModel.Day(start), ArchiveItemViewModel.Day(start.AddDays(6)));
        CountText = string.Format(CultureInfo.CurrentCulture, Strings.ArchiveWeekCountFormat, items.Count);
    }
}
