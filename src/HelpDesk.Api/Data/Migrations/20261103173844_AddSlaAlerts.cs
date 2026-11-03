using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SlaAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<int>(type: "int", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaAlerts_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // SQL a mano: EF no sabe expresar IGNORE_DUP_KEY. Con esta opción, un INSERT que trae una alerta que ya existe
            // descarta esa fila sin error, y OUTPUT solo devuelve las insertadas (ver SlaAlertScanner).
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX [IX_SlaAlerts_TicketId_Level]
                ON [SlaAlerts] ([TicketId], [Level])
                WITH (IGNORE_DUP_KEY = ON);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlaAlerts");
        }
    }
}
