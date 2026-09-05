using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoTask.Data.Migrations
{
    /// <inheritdoc />
    public partial class TerminalAiRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 廃止した状態のまま残っている行を Cancelled に寄せる（仕様 §9）。
            // 端末のプロセスはもう追えないので、成功でも失敗でもなく「追跡をやめた」が正しい。
            // Running / Pending もここに含める。旧実装のまま MoTask が落ちていると、次に上がった
            // ときの復旧処理を経ずに Running/Pending の行が残ることがあり、JobFolder = '' のまま
            // （＝1 つ前のマイグレーションが埋めた値）だと新しい RecoverOnStartupAsync も拾えず、
            // そのタスクへの AI 依頼が永久にブロックされる。JobFolder = '' の条件で、この作り替え
            // より後に生まれた行（実ジョブフォルダを持つ）を巻き込まないようにする。
            migrationBuilder.Sql(
                "UPDATE AiJobs SET Status = 'Cancelled', EndedAt = COALESCE(EndedAt, StartedAt) " +
                "WHERE Status IN ('AwaitingApproval','Suspended','Running','Pending') AND JobFolder = ''");

            // 列挙は文字列で保存されている。消した名前が残っていると読み出しで例外になるので、
            // 承認イベントは System に寄せる（Payload は原文のまま残る）。
            migrationBuilder.Sql(
                "UPDATE AiJobEvents SET Kind = 'System' WHERE Kind IN ('PermissionAsked', 'PermissionDecided')");

            migrationBuilder.DropTable(
                name: "AiPermissionRules");

            migrationBuilder.DropColumn(
                name: "TotalCostUsd",
                table: "AiJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TotalCostUsd",
                table: "AiJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiPermissionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPermissionRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiPermissionRules_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiPermissionRules_ProjectId",
                table: "AiPermissionRules",
                column: "ProjectId");
        }
    }
}
