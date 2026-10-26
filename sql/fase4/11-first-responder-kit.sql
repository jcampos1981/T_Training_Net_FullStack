/* =====================================================================================
   Fase 4 · Bloque 4 · First Responder Kit: un diagnóstico completo en cuatro llamadas.

   Procedimientos gratuitos (licencia MIT) de Brent Ozar y la comunidad. Se instalan una vez, en master,
   con la versión fijada (en esta máquina quedó en H:\DevTools\sql-tools\first-responder-kit\20260708):
     https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit/releases/tag/20260708
     sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d master -C -I -i Install-All-Scripts.sql

     1. sp_Blitz        Salud del servidor, por prioridad (1 = urgente ... 250 = informativo).
     2. sp_BlitzIndex   Índices duplicados, sin uso o faltantes. @Mode = 4 incluye los de prioridad baja.
     3. sp_BlitzCache   Las consultas más caras del caché de planes, con advertencias de cada plan.
                        Solo ve lo que sigue en el caché: para la historia, Query Store (06-query-store.sql).
     4. sp_BlitzLock    Resumen de los deadlocks que guardó system_health.
   Para un problema que está pasando AHORA: sp_BlitzFirst @Seconds = 30 (qué cambió en esos segundos)
   y sp_BlitzWho (quién ejecuta qué).

   Son pistas, no órdenes: en este proyecto marcó como "duplicados" un índice filtrado y uno completo
   que sirven a consultas distintas, y pidió un índice que copiaba la tabla (diario de tuning, caso 13).

   Uso: sqlcmd -S 127.0.0.1,1433 -U sa -P <clave> -d master -C -I -f 65001 -i sql\fase4\11-first-responder-kit.sql -o blitz.txt
   ===================================================================================== */
SET NOCOUNT ON;

EXEC master.dbo.sp_Blitz @CheckUserDatabaseObjects = 1, @CheckServerInfo = 0;
EXEC master.dbo.sp_BlitzIndex @DatabaseName = N'HelpDesk', @Mode = 4;
EXEC master.dbo.sp_BlitzCache @DatabaseName = N'HelpDesk', @SortOrder = N'reads', @Top = 10;
EXEC master.dbo.sp_BlitzLock @DatabaseName = N'HelpDesk';
