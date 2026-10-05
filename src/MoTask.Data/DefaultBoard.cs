using MoTask.Core;
using MoTask.Core.Model;

namespace MoTask.Data;

/// <summary>初回起動時に投入する既定のボード。</summary>
public static class DefaultBoard
{
    public static string Name => Messages.DefaultBoardName;

    public static Board Create() => new()
    {
        Name = Name,
        Columns =
        {
            new Column { Name = Messages.DefaultColumnBacklog, Order = 0, Role = ColumnRole.Backlog },
            new Column { Name = Messages.DefaultColumnToday, Order = 1, Role = ColumnRole.Today },
            new Column { Name = Messages.DefaultColumnActive, Order = 2, Role = ColumnRole.Active },
            new Column { Name = Messages.DefaultColumnReview, Order = 3, Role = ColumnRole.Review },
            new Column { Name = Messages.DefaultColumnDone, Order = 4, Role = ColumnRole.Done },
        },
    };
}
