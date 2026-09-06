using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoTask.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropAiJobEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiJobEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiJobEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false, defaultValue: ""),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiJobEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiJobEvents_AiJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "AiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiJobEvents_JobId_Seq",
                table: "AiJobEvents",
                columns: new[] { "JobId", "Seq" },
                unique: true);
        }
    }
}
