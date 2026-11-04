// Prueba del monitor de SLA (Fase 7, Bloque 2) contra SQL Server, SignalR y el panel en Edge.
// Uso: tests/e2e/run.ps1 -Suite sla  (por dentro: node sla.mjs <deps> <capturas> <repo>    Contraseñas en HELPDESK_DEV_PASSWORD y MSSQL_SA.)
// El script levanta y detiene la API (con un intervalo de 2 s) y el panel. Detiene SQL Server de verdad en una sección.
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const signalR = createRequire(path.join(repo, 'tests/e2e/package.json'))('@microsoft/signalr');
const password = process.env.HELPDESK_DEV_PASSWORD;
const api = 'http://localhost:5159';
const api2 = 'http://localhost:5160';
const admin = 'https://localhost:7300';
const tag = `sla-${Date.now()}`;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

function startExe(project, url, extraEnv, onLog) {
  const child = spawn(path.join(repo, `src/${project}/bin/Debug/net10.0/${project}.exe`), [], {
    cwd: path.join(repo, `src/${project}`),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: url, ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', onLog);
  child.stderr.on('data', onLog);
  return child;
}
const waitLog = async (read, text, what) => {
  for (let i = 0; i < 400 && !read().includes(text); i++) await sleep(250);
  if (!read().includes(text)) throw new Error(`${what} no arrancó:\n${read().slice(-1500)}`);
};
const fastMonitor = { SlaMonitor__CheckInterval: '00:00:02' };

let apiProcess;
let apiLog = '';
async function startApi() {
  apiLog = '';
  apiProcess = startExe('HelpDesk.Api', api, fastMonitor, (chunk) => { apiLog += chunk; });
  await waitLog(() => apiLog, 'Application started', 'La API');
}
let api2Process;
let api2Log = '';

function sqlRun(statement) {
  return execFileSync('docker', ['exec', '-e', `SQLCMDPASSWORD=${process.env.MSSQL_SA}`, 'helpdesk-sqlserver-1', '/opt/mssql-tools18/bin/sqlcmd',
    '-C', '-I', '-b', '-S', 'localhost', '-U', 'sa', '-d', 'HelpDesk', '-h', '-1', '-W', '-Q', `SET NOCOUNT ON; ${statement}`], { stdio: 'pipe' }).toString().trim();
}
const sqlCount = (statement) => Number(sqlRun(statement));
const sqlHealthy = () => execFileSync('docker', ['inspect', '-f', '{{.State.Health.Status}}', 'helpdesk-sqlserver-1']).toString().trim() === 'healthy';

async function jwt(email, base = api) {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${base}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
    if (response.ok) return (await response.json()).accessToken;
    if (response.status !== 429 || attempt > 8) throw new Error(`token ${email}: ${response.status}`);
    await sleep(10_000);
  }
}
async function callApi(method, url, token, body, base = api) {
  const started = Date.now();
  const response = await fetch(`${base}${url}`, {
    method,
    headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), ...(body ? { 'content-type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  return { status: response.status, json: text ? JSON.parse(text) : null, ms: Date.now() - started };
}

async function listen(token, base = api) {
  const received = [];
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${base}/hubs/tickets`, { accessTokenFactory: () => token, transport: signalR.HttpTransportType.WebSockets })
    .configureLogging(signalR.LogLevel.None)
    .build();
  connection.on('SlaAlertsRaised', (payload) => received.push(payload));
  await connection.start();
  return { connection, received, clear: () => received.splice(0, received.length) };
}
const waitFor = async (condition, ms = 6000) => {
  const start = Date.now();
  while (Date.now() - start < ms) { if (await condition()) return true; await sleep(100); }
  return condition();
};
const sum = (events, field) => events.reduce((total, event) => total + event[field], 0);

const created = [];
let lauraJwt;
const OPEN = "Status IN (N'Open', N'InProgress')";

try {
  console.log('--- 0. Limpieza de refresh tokens: se prepara un token vencido ---');
  sqlRun('TRUNCATE TABLE dbo.SlaAlerts;');
  const tokensBefore = sqlCount('SELECT COUNT(*) FROM dbo.RefreshTokens WHERE ExpiresAt > SYSDATETIMEOFFSET();');
  sqlRun(`INSERT INTO dbo.RefreshTokens (Id, UserId, TokenHash, FamilyId, SecurityStamp, CreatedAt, ExpiresAt)
          SELECT TOP (1) NEWID(), u.Id, CRYPT_GEN_RANDOM(32), NEWID(), N'x', DATEADD(DAY, -30, SYSDATETIMEOFFSET()), DATEADD(DAY, -1, SYSDATETIMEOFFSET()) FROM dbo.AspNetUsers AS u;`);
  check('Hay un refresh token vencido en la tabla', sqlCount('SELECT COUNT(*) FROM dbo.RefreshTokens WHERE ExpiresAt <= SYSDATETIMEOFFSET();') === 1);

  console.log('--- 1. Primer arranque: decenas de miles de tickets ya vencidos ---');
  const openTotal = sqlCount(`SELECT COUNT(*) FROM dbo.Tickets WHERE ${OPEN};`);
  const openPedro = sqlCount(`SELECT COUNT(*) FROM dbo.Tickets WHERE ${OPEN} AND AssignedTo = N'pedro';`);
  const firstRunStart = Date.now();
  await startApi();
  lauraJwt = await jwt('laura@helpdesk.local');
  const pedroJwt = await jwt('pedro@helpdesk.local');
  const luisJwt = await jwt('luis@helpdesk.local');
  const camilaJwt = await jwt('camila@acme.local');
  const [laura, pedro, luis, camila] = await Promise.all([lauraJwt, pedroJwt, luisJwt, camilaJwt].map((token) => listen(token)));
  const everyone = [laura, pedro, luis, camila];
  const clearAll = () => everyone.forEach((client) => client.clear());

  const during = await callApi('GET', '/api/tickets/1', lauraJwt);
  check('Mientras barre, la API responde sin esperar', during.status === 200 && during.ms < 2000, `${during.ms} ms`);

  const finished = await waitFor(() => /SLA: \d+ alertas nuevas/.test(apiLog), 120_000);
  const firstRunMs = Date.now() - firstRunStart;
  await sleep(500);
  const raisedAtStart = Number(/SLA: (\d+) alertas nuevas/.exec(apiLog)?.[1]);
  check('Se crea una alerta por cada ticket abierto', finished && raisedAtStart === openTotal && sqlCount('SELECT COUNT(*) FROM dbo.SlaAlerts;') === openTotal, `${raisedAtStart} de ${openTotal}, ${Math.round(firstRunMs / 1000)} s desde el arranque`);
  check('Supervisión recibe UN solo aviso, con el total', laura.received.length === 1 && laura.received[0].breached === openTotal && laura.received[0].ticket === null, JSON.stringify(laura.received[0]));
  check('Un agente recibe UN solo aviso, con lo suyo', pedro.received.length === 1 && pedro.received[0].breached === openPedro, JSON.stringify(pedro.received[0]));
  check('Un cliente no recibe nada', camila.received.length === 0);
  check('Se eliminó el refresh token vencido, y solo ese',
    sqlCount('SELECT COUNT(*) FROM dbo.RefreshTokens WHERE ExpiresAt <= SYSDATETIMEOFFSET();') === 0
      && sqlCount('SELECT COUNT(*) FROM dbo.RefreshTokens WHERE ExpiresAt > SYSDATETIMEOFFSET();') >= tokensBefore
      && apiLog.includes('Refresh tokens vencidos eliminados: 1'));
  clearAll();
  await sleep(6000);
  check('Las vueltas siguientes no repiten nada (3 vueltas)', everyone.every((client) => client.received.length === 0) && sqlCount('SELECT COUNT(*) FROM dbo.SlaAlerts;') === openTotal);

  console.log('--- 2. Un ticket llega a "en riesgo" ---');
  const created1 = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} crítico`, priority: 'Critical', assignedTo: 'pedro' });
  created.push(created1.json.id);
  const ticketId = created1.json.id;
  check('Recién creado: en plazo, y vence en 4 h', created1.json.slaState === 'OnTrack' && Math.abs(new Date(created1.json.slaDeadline) - new Date(created1.json.createdAt) - 4 * 3600_000) < 1000, `${created1.json.slaState} ${created1.json.slaDeadline}`);
  await sleep(2500);
  check('Sin alerta mientras está en plazo', pedro.received.length === 0 && sqlCount(`SELECT COUNT(*) FROM dbo.SlaAlerts WHERE TicketId = ${ticketId};`) === 0);

  sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(MINUTE, -210, CreatedAt) WHERE Id = ${ticketId};`); // 3,5 h de 4 h: 87 %
  const shifted = (await callApi('GET', `/api/tickets/${ticketId}`, lauraJwt)).json;
  check('La API lo evalúa en riesgo', shifted.slaState === 'AtRisk', shifted.slaState);
  check('Llega el aviso al agente', await waitFor(() => pedro.received.length > 0, 5000));
  const atRisk = pedro.received[0];
  check('El aviso dice cuál es', atRisk?.atRisk === 1 && atRisk.breached === 0 && atRisk.ticket?.id === ticketId && atRisk.ticket?.title === `${tag} crítico`, JSON.stringify(atRisk));
  await waitFor(() => laura.received.length > 0, 2000);
  check('Supervisión también lo recibe', laura.received[0]?.ticket?.id === ticketId);
  check('Otro agente y los clientes no', luis.received.length === 0 && camila.received.length === 0);

  const afterAlert = (await callApi('GET', `/api/tickets/${ticketId}`, lauraJwt)).json;
  check('La alerta NO cambia la versión del ticket', afterAlert.version === shifted.version, `${shifted.version} → ${afterAlert.version}`);
  const sameVersionSave = await callApi('PUT', `/api/tickets/${ticketId}`, lauraJwt, { title: afterAlert.title, priority: 'Critical', status: 'Open', assignedTo: 'pedro', version: shifted.version });
  check('Quien estaba editando puede guardar: sin 409 falso', sameVersionSave.status === 200, `${sameVersionSave.status}`);
  clearAll();
  await sleep(5000);
  check('No se repite en las vueltas siguientes', everyone.every((client) => client.received.length === 0));

  console.log('--- 3. Consultar las alertas ---');
  const pedroList = await callApi('GET', '/api/sla/alerts?pageSize=5', pedroJwt);
  check('El agente la ve primero: la más reciente', pedroList.status === 200 && pedroList.json.items[0].ticketId === ticketId && pedroList.json.items[0].level === 'AtRisk', `${pedroList.ms} ms`);
  check('Todas las de la página son suyas', pedroList.json.items.every((alert) => alert.assignedTo === 'pedro'));
  const luisList = await callApi('GET', '/api/sla/alerts?pageSize=100', luisJwt);
  check('Otro agente no la ve', luisList.status === 200 && luisList.json.items.every((alert) => alert.ticketId !== ticketId && alert.assignedTo === 'luis'), `${luisList.ms} ms`);
  const lauraRisk = await callApi('GET', '/api/sla/alerts?level=AtRisk', lauraJwt);
  check('Supervisión filtra por nivel', lauraRisk.status === 200 && lauraRisk.json.items.some((alert) => alert.ticketId === ticketId) && lauraRisk.json.items.every((alert) => alert.level === 'AtRisk'), `${lauraRisk.json.items.length} en riesgo, ${lauraRisk.ms} ms`);
  const page1 = await callApi('GET', '/api/sla/alerts?pageSize=50', lauraJwt);
  const page2 = await callApi('GET', `/api/sla/alerts?pageSize=50&after=${page1.json.nextAfter}`, lauraJwt);
  const ids = [...page1.json.items, ...page2.json.items].map((alert) => alert.id);
  check('Keyset de más nueva a más antigua, sin repetir', page1.json.items.length === 50 && new Set(ids).size === 100 && ids.every((id, index) => index === 0 || id < ids[index - 1]), `${page1.ms} ms y ${page2.ms} ms`);
  check('Trae el vencimiento calculado', new Date(pedroList.json.items[0].deadline).getTime() === new Date(shifted.slaDeadline).getTime());
  check('Sin credenciales: 401', (await callApi('GET', '/api/sla/alerts')).status === 401);
  check('Un cliente no puede: 403', (await callApi('GET', '/api/sla/alerts', camilaJwt)).status === 403);
  check('Un nivel inventado: 400', (await callApi('GET', '/api/sla/alerts?level=Nada', lauraJwt)).status === 400);

  console.log('--- 4. El mismo ticket vence ---');
  sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(MINUTE, -60, CreatedAt) WHERE Id = ${ticketId};`); // 4,5 h
  check('Llega el aviso de vencido', await waitFor(() => pedro.received.some((event) => event.breached === 1 && event.ticket?.id === ticketId), 5000));
  check('Quedan dos alertas guardadas', sqlCount(`SELECT COUNT(*) FROM dbo.SlaAlerts WHERE TicketId = ${ticketId};`) === 2);
  const afterBreach = await callApi('GET', '/api/sla/alerts?pageSize=100', pedroJwt);
  const mine = afterBreach.json.items.filter((alert) => alert.ticketId === ticketId);
  check('La lista lo muestra una vez, con la más grave', mine.length === 1 && mine[0].level === 'Breached', JSON.stringify(mine.map((alert) => alert.level)));
  const riskAfter = await callApi('GET', '/api/sla/alerts?level=AtRisk', lauraJwt);
  check('Ya no aparece entre los que están en riesgo', riskAfter.json.items.every((alert) => alert.ticketId !== ticketId));
  clearAll();

  console.log('--- 5. Cerrado, sin asignar, cliente y varios a la vez ---');
  const current = (await callApi('GET', `/api/tickets/${ticketId}`, lauraJwt)).json;
  await callApi('PUT', `/api/tickets/${ticketId}`, lauraJwt, { title: current.title, priority: current.priority, status: 'Resolved', assignedTo: 'pedro', version: current.version });
  const resolved = (await callApi('GET', `/api/tickets/${ticketId}`, lauraJwt)).json;
  const afterResolve = await callApi('GET', '/api/sla/alerts?pageSize=100', pedroJwt);
  check('Resuelto: deja de aparecer, y su estado es Closed', resolved.slaState === 'Closed' && afterResolve.json.items.every((alert) => alert.ticketId !== ticketId), resolved.slaState);

  const portal = await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `${tag} del portal`, priority: 'High', description: 'Descripción de prueba.' });
  created.push(portal.json.id);
  clearAll();
  sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(HOUR, -7, CreatedAt) WHERE Id = ${portal.json.id};`); // 7 h de 8 h
  check('Sin asignar: supervisión recibe la alerta', await waitFor(() => laura.received.some((event) => event.ticket?.id === portal.json.id), 5000));
  await sleep(300);
  check('Y ningún agente', pedro.received.length === 0 && luis.received.length === 0);
  check('El cliente no se entera: el SLA es interno', camila.received.length === 0);
  const portalView = await callApi('GET', `/api/portal/tickets/${portal.json.id}`, camilaJwt);
  check('El contrato del portal no trae el SLA', portalView.status === 200 && !('slaState' in portalView.json) && !('slaDeadline' in portalView.json), Object.keys(portalView.json).join(','));
  clearAll();

  const several = [];
  for (let i = 0; i < 3; i++) {
    const response = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} varios ${i}`, priority: 'High', assignedTo: 'pedro' });
    several.push(response.json.id);
    created.push(response.json.id);
  }
  sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(HOUR, -7, CreatedAt) WHERE Id IN (${several.join(',')});`);
  check('Varios en la misma vuelta: un aviso con la cantidad, sin ticket', await waitFor(() => pedro.received.length > 0, 5000) && pedro.received.length === 1 && pedro.received[0].atRisk === 3 && pedro.received[0].ticket === null, JSON.stringify(pedro.received[0]));

  const victim = several.pop();
  created.splice(created.indexOf(victim), 1);
  await callApi('DELETE', `/api/tickets/${victim}`, lauraJwt);
  check('Borrar el ticket borra sus alertas (cascada)', sqlCount(`SELECT COUNT(*) FROM dbo.SlaAlerts WHERE TicketId = ${victim};`) === 0);
  clearAll();

  console.log('--- 6. Dos instancias de la API a la vez ---');
  api2Process = startExe('HelpDesk.Api', api2, fastMonitor, (chunk) => { api2Log += chunk; });
  await waitLog(() => api2Log, 'Application started', 'La segunda API');
  const pedroOn2 = await listen(pedroJwt, api2);
  await sleep(2500);
  check('La segunda instancia no repite nada de lo que ya existe', !/SLA: \d+ alertas nuevas/.test(api2Log), 'sin alertas nuevas');

  const twins = [];
  for (let i = 0; i < 20; i++) {
    const response = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} gemelo ${i}`, priority: 'Medium', assignedTo: 'pedro' });
    twins.push(response.json.id);
    created.push(response.json.id);
  }
  pedro.clear();
  pedroOn2.clear();
  apiLog = '';
  api2Log = '';
  sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(HOUR, -20, CreatedAt) WHERE Id IN (${twins.join(',')});`);
  await waitFor(() => sum(pedro.received, 'atRisk') >= 20 && sum(pedroOn2.received, 'atRisk') >= 20, 8000);
  await sleep(4500);
  const fromFirst = sum(pedro.received, 'atRisk');
  const fromSecond = sum(pedroOn2.received, 'atRisk');
  // Con el backplane, quien está conectado a cualquiera de las dos recibe el aviso, salga de donde salga.
  check('Cada alerta se avisa exactamente una vez a cada conexión', fromFirst === 20 && fromSecond === 20, `conexión en la primera: ${fromFirst}, en la segunda: ${fromSecond}`);
  check('Una sola alerta por ticket en la base', sqlCount(`SELECT COUNT(*) FROM dbo.SlaAlerts WHERE TicketId IN (${twins.join(',')});`) === 20);

  // Las dos compiten por TODO el backlog, en lotes, al mismo tiempo.
  apiLog = '';
  api2Log = '';
  const rowsBefore = sqlCount('SELECT COUNT(*) FROM dbo.SlaAlerts;');
  sqlRun('TRUNCATE TABLE dbo.SlaAlerts;');
  const raisedBy = (log) => [...log.matchAll(/SLA: (\d+) alertas nuevas/g)].reduce((total, match) => total + Number(match[1]), 0);
  await waitFor(() => raisedBy(apiLog) + raisedBy(api2Log) >= rowsBefore - 1 * 2, 150_000); // tolera lo que se borró arriba
  await sleep(5000);
  const totalRaised = raisedBy(apiLog) + raisedBy(api2Log);
  const totalRows = sqlCount('SELECT COUNT(*) FROM dbo.SlaAlerts;');
  const dups = sqlCount('SELECT COUNT(*) FROM (SELECT TicketId, Level FROM dbo.SlaAlerts GROUP BY TicketId, Level HAVING COUNT(*) > 1) AS d;');
  check('Backlog completo compitiendo: lo informado coincide con la tabla', totalRaised === totalRows && dups === 0, `primera: ${raisedBy(apiLog)}, segunda: ${raisedBy(api2Log)}, filas: ${totalRows}`);
  const failures = [...apiLog.matchAll(/SlaMonitor falló/g), ...api2Log.matchAll(/SlaMonitor falló/g)].length;
  console.log(`      vueltas con error durante la competencia: ${failures}`);
  await pedroOn2.connection.stop();
  api2Process.kill();
  api2Process = undefined;
  everyone.forEach((client) => client.clear());

  console.log('--- 7. SQL Server se detiene con la API corriendo ---');
  apiLog = '';
  execFileSync('docker', ['stop', 'helpdesk-sqlserver-1'], { stdio: 'pipe' });
  const logged = await waitFor(() => apiLog.includes('SlaMonitor falló'), 90_000);
  check('Una vuelta falla y queda en el log', logged);
  check('La API sigue viva', (await fetch(`${api}/openapi/v1.json`).then((response) => response.status).catch(() => 0)) === 200);
  execFileSync('docker', ['start', 'helpdesk-sqlserver-1'], { stdio: 'pipe' });
  await waitFor(sqlHealthy, 180_000);
  await waitFor(async () => (await callApi('GET', '/api/me', lauraJwt).then((response) => response.status).catch(() => 0)) === 200, 60_000);
  const recovered = await waitFor(async () => (await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} tras el corte`, priority: 'Critical', assignedTo: 'pedro' }).then((response) => {
    if (response.status === 201) { created.push(response.json.id); return true; }
    return false;
  }).catch(() => false)), 120_000);
  const afterOutage = created.at(-1);
  everyone.forEach((client) => client.clear());
  sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(MINUTE, -300, CreatedAt) WHERE Id = ${afterOutage};`);
  check('Al volver SQL Server, el monitor sigue trabajando solo', recovered && await waitFor(() => pedro.received.some((event) => event.ticket?.id === afterOutage && event.breached === 1), 30_000));

  console.log('--- 8. Panel en Edge ---');
  let adminLog = '';
  const adminProcess = startExe('HelpDesk.Admin', admin, {}, (chunk) => { adminLog += chunk; });
  await waitLog(() => adminLog, 'Application started', 'El panel');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const consoleErrors = [];
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1360, height: 900 } });
    const page = await context.newPage();
    page.on('console', (message) => { if (message.type() === 'error' && (message.location().url ?? '').startsWith(admin)) consoleErrors.push(message.text()); });
    page.on('pageerror', (error) => consoleErrors.push(error.message));
    const has = async (locator, ms = 7000) => locator.first().waitFor({ timeout: ms }).then(() => true).catch(() => false);
    const snackbar = (text) => page.locator('.mud-snackbar', { hasText: text });
    const shot = (name) => page.screenshot({ path: path.join(shotsDir, `f7-sla-${name}.png`) });
    const countConnections = () => (apiLog.match(/Conexi[^ ]+ al hub abierta/g) ?? []).length;

    const connectionsBefore = countConnections();
    await page.goto(`${admin}/tickets`);
    await page.waitForURL(/localhost:8080/);
    await page.fill('#username', 'pedro');
    await page.fill('#password', password);
    await page.click('#kc-login');
    await page.waitForURL((url) => url.origin === admin && !url.pathname.startsWith('/signin-oidc'));
    await page.locator('.mud-table').waitFor();
    await waitFor(() => countConnections() > connectionsBefore, 8000);
    check('La lista muestra la columna SLA', await has(page.getByRole('columnheader', { name: 'SLA' })));
    await shot('lista');

    const liveTicket = (await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} en pantalla <img src=x onerror="window.__xss=1">`, priority: 'Critical', assignedTo: 'pedro' })).json;
    created.push(liveTicket.id);
    await has(snackbar(`Ticket nuevo ${liveTicket.code}`));
    await page.goto(`${admin}/tickets/${liveTicket.id}`);
    const sla = page.getByTestId('ticket-sla');
    check('El detalle muestra el SLA y cuándo vence', await has(sla.getByText('En plazo')) && (await sla.innerText()).includes(':'), (await sla.innerText()).replace(/\s+/g, ' '));

    sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(MINUTE, -210, CreatedAt) WHERE Id = ${liveTicket.id};`);
    check('Aparece el aviso de SLA en riesgo', await has(snackbar(`SLA en riesgo: ${liveTicket.code}`)));
    check('El título con HTML se muestra como texto', await has(snackbar('<img src=x')) && await page.locator('.mud-snackbar img').count() === 0 && await page.evaluate(() => window.__xss) === undefined);
    check('El detalle cambia a "En riesgo" sin recargar', await has(sla.getByText('En riesgo')));
    await shot('detalle-aviso');

    await page.getByRole('link', { name: 'Alertas de SLA' }).click();
    await page.getByRole('heading', { name: 'Alertas de SLA' }).waitFor();
    const rows = page.locator('.mud-table tbody tr');
    await rows.first().waitFor();
    check('/alertas: la más reciente va primero', (await rows.first().innerText()).includes(liveTicket.code), (await rows.first().innerText()).replace(/\s+/g, ' ').slice(0, 90));
    await shot('alertas');
    await page.getByTestId('filter-level').click(); // el contenedor ocupa todo el ancho: su centro queda fuera del control
    await page.locator('.mud-popover-open .mud-list-item', { hasText: 'En riesgo' }).first().click();
    await page.waitForURL(/nivel=AtRisk/);
    check('Filtrar por nivel va a la URL y solo deja las que corresponden', await waitFor(async () => {
      const texts = await rows.allInnerTexts();
      return texts.length > 0 && texts.every((text) => text.includes('En riesgo'));
    }, 5000));

    sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(MINUTE, -60, CreatedAt) WHERE Id = ${liveTicket.id};`);
    check('Aparece el aviso de SLA vencido, en rojo', await has(page.locator('.mud-alert-filled-error, .mud-snackbar.mud-alert-filled-error', { hasText: `SLA vencido: ${liveTicket.code}` })));
    check('/alertas avisa que hay cambios, sin mover las filas', await has(page.getByTestId('alerts-changes')));
    await page.getByTestId('alerts-changes').getByRole('button', { name: 'Actualizar' }).click();
    check('Actualizar: ya no está entre los que están en riesgo', await waitFor(async () => {
      if (await page.getByTestId('alerts-changes').count() > 0) return false;
      const loaded = await page.getByTestId('no-alerts').count() > 0 || await rows.count() > 0;
      return loaded && (await rows.allInnerTexts()).every((text) => !text.includes(liveTicket.code));
    }, 5000));
    await snackbar(`SLA vencido: ${liveTicket.code}`).getByRole('button', { name: 'Ver' }).click();
    await page.waitForURL(`**/tickets/${liveTicket.id}`);
    check('"Ver" lleva al ticket, ya vencido', await has(page.getByTestId('ticket-sla').getByText('Vencido')));

    const bulk = [];
    for (let i = 0; i < 2; i++) {
      const response = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} panel ${i}`, priority: 'Critical', assignedTo: 'pedro' });
      bulk.push(response.json.id);
      created.push(response.json.id);
    }
    await sleep(300);
    sqlRun(`UPDATE dbo.Tickets SET CreatedAt = DATEADD(MINUTE, -300, CreatedAt) WHERE Id IN (${bulk.join(',')});`);
    check('Varios a la vez: un solo aviso, con la cantidad', await has(snackbar('2 tickets con el SLA vencido')));
    await shot('aviso-varios');
    await snackbar('2 tickets con el SLA vencido').getByRole('button', { name: 'Ver' }).click();
    await page.waitForURL('**/alertas');
    check('"Ver" lleva a /alertas', true);

    check('Sin errores de consola en el navegador', consoleErrors.length === 0, consoleErrors.slice(0, 2).join(' | '));
    const serverErrors = adminLog.split('\n').filter((line) => /^fail:|Unhandled exception/.test(line));
    check('Sin errores en el log del panel', serverErrors.length === 0, serverErrors.slice(0, 2).join(' | '));
  } finally {
    await browser.close();
    adminProcess.kill();
  }

  await Promise.all(everyone.map((client) => client.connection.stop()));
  const unexpected = apiLog.split('\n').filter((line) => /^fail:/.test(line) && !/SlaMonitor/.test(line) && !/DbUpdate|Microsoft.EntityFrameworkCore/.test(line));
  console.log(`      líneas fail: en el log de la API tras el corte: ${unexpected.length}`);
} finally {
  const cleaner = await jwt('laura@helpdesk.local').catch(() => lauraJwt);
  for (const id of created) await callApi('DELETE', `/api/tickets/${id}`, cleaner).catch(() => {});
  api2Process?.kill();
  apiProcess?.kill();
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
