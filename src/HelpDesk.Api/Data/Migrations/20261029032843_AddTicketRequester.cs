using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketRequester : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Columna NULL y sin valor por defecto: SQL Server solo cambia los metadatos, sin tocar los 2,5 M de filas.
            // El índice sí recorre la tabla, pero queda casi vacío: solo guarda las filas con RequestedBy.
            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "Tickets",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RequestedBy",
                table: "Tickets",
                column: "RequestedBy",
                filter: "[RequestedBy] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_RequestedBy",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "Tickets");
        }
    }
}
