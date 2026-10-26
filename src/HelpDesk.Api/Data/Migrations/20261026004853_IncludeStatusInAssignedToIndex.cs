using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncludeStatusInAssignedToIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_AssignedTo",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AssignedTo",
                table: "Tickets",
                column: "AssignedTo")
                .Annotation("SqlServer:Include", new[] { "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_AssignedTo",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AssignedTo",
                table: "Tickets",
                column: "AssignedTo");
        }
    }
}
