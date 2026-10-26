using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpDesk.Api.Data.Migrations
{
    /// <summary>
    /// Optimized locking (SQL Server 2025): una transacción deja un solo bloqueo XACT en vez de uno por fila,
    /// así que la reasignación masiva ya no bloquea la tabla entera ni choca con la reasignación inversa
    /// (diario de tuning, casos 9 a 11). Requiere ADR (Accelerated Database Recovery), que además vuelve
    /// instantáneo el ROLLBACK. Es configuración de la base que la app necesita, igual que el RCSI que EF Core
    /// activa al crearla: por eso va en una migración y no en un script aparte.
    /// </summary>
    public partial class EnableOptimizedLocking : Migration
    {
        // - SQL dinámico: en versiones anteriores la opción OPTIMIZED_LOCKING no existe y el lote ni compilaría.
        // - EngineEdition 5 = Azure SQL Database, que ya trae ADR y optimized locking encendidos.
        // - Solo cambia lo que hace falta: aplicar la migración en una base ya configurada no hace nada.
        private const string EnableSql = """
            IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 17 AND SERVERPROPERTY('EngineEdition') <> 5
            BEGIN
                EXEC (N'IF (SELECT is_accelerated_database_recovery_on FROM sys.databases WHERE database_id = DB_ID()) = 0
                            ALTER DATABASE CURRENT SET ACCELERATED_DATABASE_RECOVERY = ON;');
                IF DATABASEPROPERTYEX(DB_NAME(), 'IsOptimizedLockingOn') = 0
                    EXEC (N'ALTER DATABASE CURRENT SET OPTIMIZED_LOCKING = ON;');
            END
            """;

        private const string DisableSql = """
            IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 17 AND SERVERPROPERTY('EngineEdition') <> 5
            BEGIN
                IF DATABASEPROPERTYEX(DB_NAME(), 'IsOptimizedLockingOn') = 1
                    EXEC (N'ALTER DATABASE CURRENT SET OPTIMIZED_LOCKING = OFF;');
                EXEC (N'ALTER DATABASE CURRENT SET ACCELERATED_DATABASE_RECOVERY = OFF;');
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ALTER DATABASE no se puede ejecutar dentro de una transacción, y EF envuelve cada migración en una.
            migrationBuilder.Sql(EnableSql, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DisableSql, suppressTransaction: true);
        }
    }
}
