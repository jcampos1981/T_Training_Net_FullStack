using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenTicketsFilteredIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Open_AssignedTo",
                table: "Tickets",
                column: "AssignedTo",
                filter: "[Status] IN (N'Open', N'InProgress')")
                .Annotation("SqlServer:Include", new[] { "Status", "Priority", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_Open_AssignedTo",
                table: "Tickets");
        }
    }
}
