using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoTask.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanningRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    JobFolder = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessedLines = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanningRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TriageCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanningRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    From = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Title = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Link = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuggestedDueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SuggestedProject = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    SuggestedAction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SuggestedMergeTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ResultTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriageCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TriageCandidates_PlanningRuns_PlanningRunId",
                        column: x => x.PlanningRunId,
                        principalTable: "PlanningRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TriageCandidates_Tasks_ResultTaskId",
                        column: x => x.ResultTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanningRuns_Date",
                table: "PlanningRuns",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_PlanningRuns_Status",
                table: "PlanningRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TriageCandidates_ExternalId",
                table: "TriageCandidates",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriageCandidates_PlanningRunId",
                table: "TriageCandidates",
                column: "PlanningRunId");

            migrationBuilder.CreateIndex(
                name: "IX_TriageCandidates_ResultTaskId",
                table: "TriageCandidates",
                column: "ResultTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TriageCandidates_Status",
                table: "TriageCandidates",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TriageCandidates");

            migrationBuilder.DropTable(
                name: "PlanningRuns");
        }
    }
}
