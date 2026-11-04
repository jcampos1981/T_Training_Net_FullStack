using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxAndWorkerTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TraceParent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                column: "Id",
                filter: "[PublishedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Published",
                table: "OutboxMessages",
                column: "PublishedAt",
                filter: "[PublishedAt] IS NOT NULL");

            // Las tablas del worker, en su propio esquema. Él no tiene DbContext ni aplica migraciones: un solo programa cambia el esquema.
            migrationBuilder.Sql("CREATE SCHEMA worker;");

            // IGNORE_DUP_KEY: un mensaje repetido inserta 0 filas en vez de lanzar un error. Así el worker reconoce que ya lo procesó.
            migrationBuilder.Sql("""
                CREATE TABLE worker.ProcessedMessages (
                    MessageId uniqueidentifier NOT NULL,
                    ProcessedAt datetimeoffset NOT NULL,
                    CONSTRAINT PK_ProcessedMessages PRIMARY KEY (MessageId) WITH (IGNORE_DUP_KEY = ON)
                );
                """);

            migrationBuilder.Sql("""
                CREATE TABLE worker.SentEmails (
                    Id bigint IDENTITY NOT NULL CONSTRAINT PK_SentEmails PRIMARY KEY,
                    MessageId uniqueidentifier NOT NULL,
                    Recipient nvarchar(256) NOT NULL,
                    Subject nvarchar(400) NOT NULL,
                    TicketCode nvarchar(32) NOT NULL,
                    SentAt datetimeoffset NOT NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE worker.SentEmails;");
            migrationBuilder.Sql("DROP TABLE worker.ProcessedMessages;");
            migrationBuilder.Sql("DROP SCHEMA worker;");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
