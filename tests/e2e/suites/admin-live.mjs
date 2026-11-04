// Prueba de punta a punta de los avisos en vivo del panel Blazor (Fase 7, Bloque 1) en Edge headless.
// Uso: tests/e2e/run.ps1 -Suite admin-live  (por dentro: node admin-live.mjs <deps> <capturas> <repo> [largo]    Contraseña en HELPDESK_DEV_PASSWORD, la de SA en MSSQL_SA.)
//   El script levanta y detiene la API y el panel. "largo": espera a que venza el token de Keycloak (unos 5 minutos).
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo, long] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const admin = 'https://localhost:7300';
const api = 'http://localhost:5159';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

function startExe(project, url, onLog) {
  const child = spawn(path.join(repo, `src/${project}/bin/Debug/net10.0/${project}.exe`), [], {
    cwd: path.join(repo, `src/${project}`),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: url },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', onLog);
  child.stderr.on('data', onLog);
  return child;
}
const waitLog = async (read, text, what) => {
  for (let i = 0; i < 240 && !read().includes(text); i++) await sleep(250);
  if (!read().includes(text)) throw new Error(`${what} no arrancó:\n${read().slice(-2000)}`);
};

let apiProcess;
let apiLog = '';
async function startApi() {
  apiLog = '';
  apiProcess = startExe('HelpDesk.Api', api, (chunk) => { apiLog += chunk; });
  await waitLog(() => apiLog, 'Application started', 'La API');
}
let adminProcess;
let adminLog = '';
async function startAdmin() {
  adminProcess = startExe('HelpDesk.Admin', admin, (chunk) => { adminLog += chunk; });
  await waitLog(() => adminLog, 'Application started', 'El panel');
}
const countLog = (pattern) => (apiLog.match(pattern) ?? []).length;
const hubConnections = () => countLog(/Conexi[^ ]+ al hub abierta/g);
const finishedSockets = () => countLog(/GET \/hubs\/tickets[^ ]* → /g);

async function jwt(email) {
  const response = await fetch(`${api}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
  if (!response.ok) throw new Error(`token ${email}: ${response.status}`);
  return (await response.json()).accessToken;
}
async function callApi(method, url, token, body) {
  const response = await fetch(`${api}${url}`, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body ? { 'content-type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  return { status: response.status, json: text ? JSON.parse(text) : null };
}

function sql(statement) {
  execFileSync('docker', ['exec', '-e', `SQLCMDPASSWORD=${process.env.MSSQL_SA}`, 'helpdesk-sqlserver-1', '/opt/mssql-tools18/bin/sqlcmd',
    '-C', '-I', '-b', '-S', 'localhost', '-U', 'sa', '-d', 'HelpDesk', '-Q', statement], { stdio: 'pipe' });
}

const browser = await chromium.launch({ channel: 'msedge', headless: true });

function track(page, label, errors) {
  page.on('console', (message) => {
    if (message.type() !== 'error') return;
    const url = message.location().url ?? '';
    if (url && !url.startsWith(admin)) return;
    errors.push(`[${label}] ${message.text()} @ ${url}`);
  });
  page.on('pageerror', (error) => errors.push(`[${label}] pageerror: ${error.message}`));
}
async function login(page, user, target = '/') {
  await page.goto(`${admin}${target}`);
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL((url) => url.origin === admin && !url.pathname.startsWith('/signin-oidc'));
}

const has = async (locator, ms = 6000) => locator.first().waitFor({ timeout: ms }).then(() => true).catch(() => false);
const gone = async (locator, ms = 6000) => locator.first().waitFor({ state: 'detached', timeout: ms }).then(() => true).catch(() => false);
const waitFor = async (condition, ms = 5000) => {
  const start = Date.now();
  while (Date.now() - start < ms) { if (await condition()) return true; await sleep(100); }
  return condition();
};

const created = [];
const tag = `vivo-${Date.now()}`;
const consoleErrors = [];
let lauraJwt;
let hubConnected = () => waitFor(() => hubConnections() > 0, 8000);

try {
  await startApi();
  await startAdmin();

  lauraJwt = await jwt('laura@helpdesk.local');
  const pedroJwt = await jwt('pedro@helpdesk.local');
  const camilaJwt = await jwt('camila@acme.local');
  const newTicket = async (title, assignedTo = 'pedro') => {
    const response = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} ${title}`, priority: 'Medium', assignedTo });
    if (response.status !== 201) throw new Error(`crear ticket: ${response.status}`);
    created.push(response.json.id);
    return response.json;
  };
  const updateAs = async (id, changes) => {
    const current = (await callApi('GET', `/api/tickets/${id}`, lauraJwt)).json;
    return callApi('PUT', `/api/tickets/${id}`, lauraJwt, { title: current.title, priority: current.priority, status: current.status, assignedTo: current.assignedTo, version: current.version, ...changes });
  };

  const pedroContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1360, height: 900 } });
  const page = await pedroContext.newPage();
  track(page, 'pedro', consoleErrors);
  let documentRequests = 0;
  page.on('request', (request) => { if (request.isNavigationRequest() && request.url().startsWith(admin)) documentRequests++; });
  const snackbar = (text) => page.locator('.mud-snackbar', { hasText: text });
  const comments = page.locator('.comment-body');
  const clearSnackbars = async () => {
    for (const close of await page.locator('.mud-snackbar .mud-snackbar-close-button, .mud-snackbar button[aria-label*="Close"], .mud-snackbar button[aria-label*="Cerrar"]').all()) {
      await close.click({ timeout: 1000 }).catch(() => {});
    }
    await gone(page.locator('.mud-snackbar'), 8000);
  };
  const shot = (name, target = page) => target.screenshot({ path: path.join(shotsDir, `f7-admin-${name}.png`) });

  console.log('--- 1. Conexión ---');
  await login(page, 'pedro');
  const loginAt = Date.now();
  await page.locator('.mud-table').waitFor();
  check('El circuito abre la conexión con el hub', await hubConnected(), `${hubConnections()} conexiones`);
  check('No muestra "sin conexión"', await page.getByTestId('live-offline').count() === 0);
  const docs = documentRequests;

  console.log('--- 2. Lista: avisa, no se recarga sola ---');
  const first = await newTicket('asignado a pedro');
  check('Aparece un aviso de ticket nuevo', await has(snackbar(`Ticket nuevo ${first.code}`)));
  check('La lista avisa que hay cambios', await has(page.getByTestId('list-changes')));
  await shot('lista-aviso');
  const listRequestsBefore = countLog(/GET \/api\/tickets\?/g);
  await sleep(500);
  check('La lista no se recargó sola', countLog(/GET \/api\/tickets\?/g) === listRequestsBefore);
  await page.getByTestId('list-changes').getByRole('button', { name: 'Actualizar' }).click();
  check('"Actualizar" recarga y quita el aviso', await gone(page.getByTestId('list-changes')) && await waitFor(() => countLog(/GET \/api\/tickets\?/g) > listRequestsBefore));
  await page.locator('.mud-table').waitFor();

  console.log('--- 3. El aviso lleva al ticket ---');
  await snackbar(`Ticket nuevo ${first.code}`).getByRole('button', { name: 'Ver' }).click();
  await page.waitForURL(`**/tickets/${first.id}`);
  check('"Ver" navega al ticket, dentro del circuito', documentRequests === docs, `${documentRequests - docs} recargas`);
  await page.getByRole('heading', { name: new RegExp(first.code) }).waitFor();
  await clearSnackbars();

  console.log('--- 4. Detalle: alguien comenta ---');
  const commentField = page.getByLabel('Agregar un comentario', { exact: true });
  await commentField.fill('Borrador sin enviar');
  await commentField.press('Tab');
  await callApi('POST', `/api/tickets/${first.id}/comments`, lauraJwt, { body: 'Comentario de laura' });
  check('El comentario aparece sin recargar', await has(comments.filter({ hasText: 'Comentario de laura' })));
  check('Aparece el aviso con quién lo hizo', await has(snackbar(`laura comentó en ${first.code}`)));
  check('El borrador sigue intacto', await commentField.inputValue() === 'Borrador sin enviar');
  await shot('detalle-comentario');
  await clearSnackbars();

  console.log('--- 5. Lo propio no genera aviso, ni se duplica ---');
  await page.getByRole('button', { name: 'Comentar', exact: true }).click();
  check('Mi comentario aparece', await has(comments.filter({ hasText: 'Borrador sin enviar' })));
  await sleep(800);
  check('Aparece una sola vez, aunque también llegó su aviso', await comments.filter({ hasText: 'Borrador sin enviar' }).count() === 1);
  check('Sin aviso por lo que hice yo', await page.locator('.mud-snackbar').count() === 0);
  check('El circuito sigue vivo tras el aviso de mi propio comentario', await page.getByTestId('api-error').count() === 0);

  console.log('--- 6. Editando: lo escrito no se pierde ---');
  await page.getByRole('button', { name: 'Editar', exact: true }).click();
  const titleField = page.getByLabel('Título', { exact: true });
  await titleField.fill(`${tag} mi título a medias`);
  await titleField.press('Tab');
  await callApi('POST', `/api/tickets/${first.id}/comments`, lauraJwt, { body: 'Comentario durante la edición' });
  check('Durante la edición, la conversación se actualiza', await has(comments.filter({ hasText: 'Comentario durante la edición' })));
  check('Un comentario no cambia la versión: no hay aviso de edición', await page.getByTestId('changed-while-editing').count() === 0);
  await updateAs(first.id, { priority: 'High' });
  check('Si otro cambia el ticket, se avisa dentro del formulario', await has(page.getByTestId('changed-while-editing')));
  check('Lo escrito sigue en el formulario', await titleField.inputValue() === `${tag} mi título a medias`);
  await shot('edicion-aviso');
  await clearSnackbars();
  await page.getByRole('button', { name: 'Guardar', exact: true }).click();
  check('Guardar lleva al conflicto, que ya sabe resolver', await has(page.getByTestId('edit-conflict')));
  await page.getByRole('button', { name: /Cargar lo actual/ }).click();
  check('"Cargar lo actual" quita los dos avisos', await gone(page.getByTestId('edit-conflict')) && await page.getByTestId('changed-while-editing').count() === 0);
  await page.getByRole('button', { name: 'Cancelar', exact: true }).click();
  check('Al dejar de editar, muestra los datos al día', await has(page.locator('.ticket-facts').getByText('Alta')));
  await clearSnackbars();

  console.log('--- 7. Cierre, reasignación y eliminación mientras se mira ---');
  await updateAs(first.id, { status: 'Closed' });
  check('Cerrado: la pantalla lo muestra', await has(page.getByTestId('ticket-closed')));
  await clearSnackbars();

  const moved = await newTicket('se lo quitan');
  await page.goto(`${admin}/tickets/${moved.id}`);
  await page.getByRole('heading', { name: new RegExp(moved.code) }).waitFor();
  await clearSnackbars();
  await updateAs(moved.id, { assignedTo: 'luis' });
  check('Reasignado a otro: la pantalla dice que ya no es suyo', await has(page.getByRole('heading', { name: 'Este ticket no está asignado a ti' })));
  check('Y el aviso dice a quién pasó', await has(snackbar(`laura reasignó ${moved.code} a luis`)));
  await clearSnackbars();

  const deleted = await newTicket('lo eliminan');
  await page.goto(`${admin}/tickets/${deleted.id}`);
  await page.getByRole('heading', { name: new RegExp(deleted.code) }).waitFor();
  await clearSnackbars();
  await callApi('DELETE', `/api/tickets/${deleted.id}`, lauraJwt);
  created.splice(created.indexOf(deleted.id), 1);
  check('Eliminado: la pantalla lo dice', await has(page.getByRole('heading', { name: 'No encontramos ese ticket' })));
  check('Eliminado: el aviso no ofrece "Ver"', await has(snackbar(`laura eliminó ${deleted.code}`)) && await snackbar(`laura eliminó ${deleted.code}`).getByRole('button', { name: 'Ver' }).count() === 0);
  await clearSnackbars();

  console.log('--- 8. Contenido hostil ---');
  const hostile = await callApi('POST', '/api/tickets', lauraJwt, { title: `<img src=x onerror="window.__xss=1">${tag}`, priority: 'Low', assignedTo: 'pedro' });
  check('El ticket hostil se creó', hostile.status === 201, `${hostile.status}`);
  created.push(hostile.json.id);
  check('El aviso muestra el título como texto', await has(snackbar('<img src=x')));
  check('No se creó ningún elemento <img>', await page.locator('.mud-snackbar img').count() === 0);
  check('Ningún script inyectado se ejecutó', await page.evaluate(() => window.__xss) === undefined);
  await clearSnackbars();

  console.log('--- 9. Reasignación masiva ---');
  const bulkAgent = `e2e-${tag}`;
  await newTicket('masivo 1', bulkAgent);
  await newTicket('masivo 2', bulkAgent);
  await sleep(300);
  const bulk = await callApi('POST', `/api/agents/${bulkAgent}/reassign`, lauraJwt, { to: 'pedro' });
  check('El agente recibe un solo aviso con la cantidad', bulk.status === 200 && await has(snackbar(`laura te asignó 2 tickets de ${bulkAgent}`)), `${bulk.status}`);
  await clearSnackbars();

  console.log('--- 10. Supervisión: dashboard en vivo y sin ruido ---');
  const lauraContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1360, height: 900 } });
  const lauraPage = await lauraContext.newPage();
  track(lauraPage, 'laura', consoleErrors);
  await login(lauraPage, 'laura', '/dashboard');
  const openTile = lauraPage.getByTestId('status-Open').locator('.stat-value');
  await openTile.waitFor();
  await waitFor(() => hubConnections() >= 2, 8000);
  const openBefore = await openTile.innerText();

  const fromPortal = await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `${tag} del portal`, priority: 'Medium', description: 'Descripción de prueba.' });
  check('El ticket del portal se creó', fromPortal.status === 201, `${fromPortal.status}`);
  created.push(fromPortal.json.id);
  const lauraSnack = (text) => lauraPage.locator('.mud-snackbar', { hasText: text });
  check('Un ticket nuevo sin asignar sí le avisa a supervisión', await has(lauraSnack(`Ticket nuevo ${fromPortal.json.code}`)));
  check('El dashboard se actualiza solo', await waitFor(async () => await openTile.innerText() !== openBefore, 9000), `${openBefore} → ${await openTile.innerText()}`);
  await shot('dashboard-aviso', lauraPage);
  check('Los avisos con "Ver" desaparecen solos (no se acumulan)', await gone(lauraPage.locator('.mud-snackbar'), 16_000));

  const pedroTicket = await newTicket('de pedro, sin ruido');
  await gone(lauraPage.locator('.mud-snackbar'), 500);
  await callApi('POST', `/api/tickets/${pedroTicket.id}/comments`, pedroJwt, { body: 'trabajo normal' });
  await sleep(1000);
  check('El trabajo normal de un agente NO le avisa a supervisión', await lauraPage.locator('.mud-snackbar').count() === 0, (await lauraPage.locator('.mud-snackbar').allInnerTexts()).join(' | '));

  await sleep(6000); // que termine la recarga pendiente
  const dashboardsBefore = countLog(/GET \/api\/dashboard/g);
  await Promise.all([1, 2, 3, 4, 5, 6, 7, 8].map((n) => callApi('POST', `/api/tickets/${pedroTicket.id}/comments`, pedroJwt, { body: `ráfaga ${n}` })));
  await sleep(7000);
  const burstQueries = countLog(/GET \/api\/dashboard/g) - dashboardsBefore;
  check('Una ráfaga de 8 cambios son 1 o 2 consultas al dashboard, no 8', burstQueries >= 1 && burstQueries <= 2, `${burstQueries}`);

  await lauraPage.getByRole('link', { name: 'Agentes' }).click();
  await lauraPage.getByRole('heading', { name: 'Agentes' }).waitFor();
  await lauraPage.locator('.mud-table').waitFor();
  const dashboardsAfterLeaving = countLog(/GET \/api\/dashboard/g);
  const agentsBefore = countLog(/GET \/api\/agents →/g);
  await callApi('POST', `/api/tickets/${pedroTicket.id}/comments`, pedroJwt, { body: 'después de salir' });
  await sleep(7000);
  check('Al salir del dashboard deja de escuchar (Dispose)', countLog(/GET \/api\/dashboard/g) === dashboardsAfterLeaving);
  check('La pantalla de agentes se actualiza sola', countLog(/GET \/api\/agents →/g) > agentsBefore);

  console.log('--- 11. Caída de la API: lo perdido se recupera ---');
  const outage = await newTicket('título antes del corte');
  await page.goto(`${admin}/tickets/${outage.id}`);
  await page.getByRole('heading', { name: new RegExp(outage.code) }).waitFor();
  const docsBeforeOutage = documentRequests;
  await waitFor(async () => await page.getByTestId('live-offline').count() === 0, 6000);
  apiProcess.kill();
  check('Sin conexión: se avisa en la barra superior', await has(page.getByTestId('live-offline'), 20_000));
  await shot('sin-conexion');
  // Con la API abajo, el cambio entra directo a la base: el aviso de ese cambio no existe, y el panel debe enterarse igual.
  sql(`UPDATE dbo.Tickets SET Title = N'${tag} título durante el corte' WHERE Id = ${outage.id};`);
  await startApi();
  check('Al volver la API, el aviso desaparece', await gone(page.getByTestId('live-offline'), 45_000));
  check('Al volver, se recupera lo que cambió durante el corte, sin recargar', await has(page.getByRole('heading', { name: 'título durante el corte' }), 8000));
  check('Sin recargar la página: es el mismo circuito', documentRequests === docsBeforeOutage, `${documentRequests - docsBeforeOutage}`);
  await clearSnackbars();
  await callApi('POST', `/api/tickets/${outage.id}/comments`, lauraJwt, { body: 'Después del corte' });
  check('Los avisos en vivo siguen funcionando', await has(comments.filter({ hasText: 'Después del corte' })));
  await clearSnackbars();

  if (long) {
    console.log('--- 12. Al cerrar el navegador, el circuito suelta su conexión ---');
    const activeBefore = await (async () => { await waitFor(() => hubConnections() >= 2, 40_000); return hubConnections() - finishedSockets(); })();
    await lauraContext.close();
    console.log(`      conexiones activas: ${activeBefore}. Esperando que Blazor libere el circuito (3 min)...`);

    console.log('--- 13. El token de Keycloak vence con la pantalla abierta ---');
    let offlineSeen = false;
    const connectionsBefore = hubConnections();
    const deadline = Date.now() + 5.5 * 60_000;
    while (Date.now() < deadline && hubConnections() === connectionsBefore) {
      offlineSeen ||= await page.getByTestId('live-offline').count() > 0;
      await sleep(500);
    }
    check('El panel se reconecta solo cuando vence el token', hubConnections() > connectionsBefore, `a los ${Math.round((Date.now() - loginAt) / 1000)} s del login`);
    await sleep(1000);
    check('El usuario no vio ningún aviso de corte', !offlineSeen && await page.getByTestId('live-offline').count() === 0);
    check('Se renovó el token en el servidor', /Access token renovado/.test(adminLog));
    const renewed = await callApi('POST', `/api/tickets/${outage.id}/comments`, lauraJwt, { body: 'Con el token nuevo' });
    check('Los avisos siguen llegando con el token nuevo', renewed.status === 201 && await has(comments.filter({ hasText: 'Con el token nuevo' })));
    await clearSnackbars();

    const released = await waitFor(() => hubConnections() - finishedSockets() <= 1, 4 * 60_000);
    check('El circuito cerrado soltó su conexión con el hub', released, `activas: ${hubConnections() - finishedSockets()}`);
  } else {
    await lauraContext.close();
  }

  console.log('--- 14. La sesión termina con la pantalla abierta ---');
  const secondTab = await pedroContext.newPage();
  await secondTab.goto(admin);
  await secondTab.getByRole('button', { name: 'Cerrar sesión' }).click();
  await secondTab.waitForURL(/localhost:8080/);
  await secondTab.close();
  // Se fuerza la reconexión: sin sesión, no hay token que pedir.
  const adminLogBefore = adminLog.length;
  apiProcess.kill();
  await sleep(4000);
  await startApi();
  check('El panel deja de intentar conectarse', await waitFor(() => adminLog.slice(adminLogBefore).includes('La sesión terminó'), 45_000));
  await sleep(4000);
  const retriesAfterEnd = (adminLog.slice(adminLog.indexOf('La sesión terminó', adminLogBefore)).match(/Sin conexión al hub/g) ?? []).length;
  check('Y no reintenta más', retriesAfterEnd === 0, `${retriesAfterEnd}`);
  await page.getByRole('link', { name: 'Tickets' }).first().click();
  check('La siguiente acción lleva al login', await page.waitForURL(/localhost:8080/, { timeout: 15_000 }).then(() => true).catch(() => false));

  check('Sin errores de consola en el navegador', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '));
  const serverErrors = adminLog.split('\n').filter((line) => /^fail:|Unhandled exception/.test(line));
  check('Sin errores en el log del panel', serverErrors.length === 0, serverErrors.slice(0, 3).join(' | '));
} finally {
  await browser.close();
  const cleaner = await (async () => { try { return await jwt('laura@helpdesk.local'); } catch { return lauraJwt; } })();
  for (const id of created) await callApi('DELETE', `/api/tickets/${id}`, cleaner).catch(() => {});
  adminProcess?.kill();
  apiProcess?.kill();
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
