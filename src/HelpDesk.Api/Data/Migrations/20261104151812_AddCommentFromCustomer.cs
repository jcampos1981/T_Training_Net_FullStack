using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentFromCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FromCustomer",
                table: "TicketComments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Lo que ya existe no dice quién lo escribió. Un cliente firma con la parte de su correo antes de la @: es lo más cercano que hay.
            migrationBuilder.Sql("""
                UPDATE c
                SET c.FromCustomer = 1
                FROM dbo.TicketComments AS c
                JOIN dbo.Tickets AS t ON t.Id = c.TicketId
                WHERE CHARINDEX('@', t.RequestedBy) > 1
                  AND c.Author = LEFT(t.RequestedBy, CHARINDEX('@', t.RequestedBy) - 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromCustomer",
                table: "TicketComments");
        }
    }
}
