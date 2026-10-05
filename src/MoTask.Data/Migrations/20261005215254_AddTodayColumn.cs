using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoTask.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTodayColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 役割 Today の列が無いボードに「今日中」を 1 列だけ入れる（仕様 2026-10-05-today-column §3.3）。
            // 位置は最初の未着手の列の右、未着手が無ければ先頭。後ろの列を 1 つずつずらしてから入れる。
            // 最初の未着手の列自身は位置より前にあってずれないので、2 本目でも同じ位置が求まる。
            // 名前は書いた時点の既定名で固定する（resx を引くと後で文言を変えたときに移行の結果が変わる）。
            migrationBuilder.Sql(
                """
                UPDATE Columns SET "Order" = "Order" + 1
                WHERE NOT EXISTS (SELECT 1 FROM Columns t WHERE t.BoardId = Columns.BoardId AND t.Role = 'Today')
                  AND "Order" >= COALESCE(
                      (SELECT MIN(b."Order") + 1 FROM Columns b WHERE b.BoardId = Columns.BoardId AND b.Role = 'Backlog'), 0);
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO Columns (BoardId, Name, "Order", WipLimit, Role)
                SELECT bd.Id, '今日中',
                       COALESCE((SELECT MIN(b."Order") + 1 FROM Columns b WHERE b.BoardId = bd.Id AND b.Role = 'Backlog'), 0),
                       NULL, 'Today'
                FROM Boards bd
                WHERE NOT EXISTS (SELECT 1 FROM Columns t WHERE t.BoardId = bd.Id AND t.Role = 'Today');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 入れた列にはもうカードが載っているかもしれないので消さない。
        }
    }
}
