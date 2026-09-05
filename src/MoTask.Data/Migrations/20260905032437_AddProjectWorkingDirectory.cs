using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoTask.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectWorkingDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkingDirectory",
                table: "Projects",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkingDirectory",
                table: "Projects");
        }
    }
}
