-- Antepón este archivo a otro script para que SQL Server devuelva el PLAN REAL (XML) de cada consulta:
--   sqlcmd ... -y 0 -i sql\fase4\plan-xml-on.sql,sql\fase4\02-linea-base.sql -o planes.txt
SET STATISTICS XML ON;
