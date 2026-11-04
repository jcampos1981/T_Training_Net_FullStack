using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A mano: EF no necesita conocer esta tabla, que solo se lee y escribe con SQL. El tipo vector es de SQL Server 2025 y Azure SQL. Las 768
            // dimensiones son las del modelo de embeddings: otro modelo con otro tamaño necesita otra columna.
            migrationBuilder.Sql("""
                CREATE TABLE dbo.TicketEmbeddings (
                    TicketId int NOT NULL CONSTRAINT PK_TicketEmbeddings PRIMARY KEY,
                    Embedding vector(768) NOT NULL,
                    Model nvarchar(100) NOT NULL,
                    EmbeddedAt datetimeoffset NOT NULL,
                    CONSTRAINT FK_TicketEmbeddings_Tickets FOREIGN KEY (TicketId) REFERENCES dbo.Tickets (Id) ON DELETE CASCADE
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE dbo.TicketEmbeddings;");
        }
    }
}
