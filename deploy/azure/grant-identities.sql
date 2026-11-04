-- Se ejecuta UNA vez, en la base HelpDesk, conectado como el administrador de Entra del servidor (no como el login de Keycloak):
--   sqlcmd -S <servidor>.database.windows.net -d HelpDesk -G -I -i deploy/azure/grant-identities.sql
--
-- Los nombres son los de las identidades de main.bicep con el prefijo por defecto. Si usaste otro prefijo, cámbialos.

-- La aplicación atiende: lee y escribe datos, y no puede cambiar el esquema.
CREATE USER [helpdesk-app] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [helpdesk-app];
ALTER ROLE db_datawriter ADD MEMBER [helpdesk-app];

-- El programa de migraciones sí lo cambia, y solo corre durante un despliegue.
CREATE USER [helpdesk-migrator] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [helpdesk-migrator];
