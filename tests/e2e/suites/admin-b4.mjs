// Prueba de punta a punta del panel Blazor (Fase 6, Bloque 4) en Edge headless.
// Uso: tests/e2e/run.ps1 -Suite admin-b4  (por dentro: node admin-b4.mjs <carpeta-deps> <carpeta-capturas> <raíz-del-repo>)
//   Contraseñas en HELPDESK_DEV_PASSWORD y KEYCLOAK_ADMIN_PASSWORD. La API debe estar corriendo.
// El script levanta el panel con RefreshBeforeExpiry=4:58: el token (5 min) "está por vencer" a los 2 s de emitido,
// así se prueba la renovación dentro del circuito sin esperar cinco minutos.
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const keycloakAdminPassword = process.env.KEYCLOAK_ADMIN_PASSWORD;
const admin = 'https://localhost:7300';
const api = 'http://localhost:5159';
const keycloak = 'http://localhost:8080';
const zone = 'Asia/Tokyo'; // lejos del servidor: si el panel usara la hora del servidor, se notaría

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

// ---- El panel como proceso hijo ----
let adminProcess;
let adminLog = '';
async function startAdmin() {
  const logStart = adminLog.length;
  adminProcess = spawn(path.join(repo, 'src/HelpDesk.Admin/bin/Debug/net10.0/HelpDesk.Admin.exe'), [], {
    cwd: path.join(repo, 'src/HelpDesk.Admin'),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: admin, Keycloak__RefreshBeforeExpiry: '00:04:58' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  adminProcess.stdout.on('data', (chunk) => { adminLog += chunk; });
  adminProcess.stderr.on('data', (chunk) => { adminLog += chunk; });
  for (let i = 0; i < 120 && !adminLog.slice(logStart).includes('Application started'); i++) await sleep(250);
  if (!adminLog.slice(logStart).includes('Application started')) throw new Error(`El panel no arrancó:\n${adminLog.slice(logStart)}`);
}
async function stopAdmin() {
  if (!adminProcess) return;
  const exited = new Promise((resolve) => adminProcess.once('exit', resolve));
  adminProcess.kill();
  await exited;
  adminProcess = undefined;
}
const refreshCount = () => (adminLog.match(/Access token renovado/g) ?? []).length;

// ---- API y Keycloak fuera del navegador: preparar datos y revocar sesiones ----
async function jwt(email) {
  const response = await fetch(`${api}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
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
async function revokeKeycloakSessions(username) {
  const tokenResponse = await fetch(`${keycloak}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    body: new URLSearchParams({ grant_type: 'password', client_id: 'admin-cli', username: 'admin', password: keycloakAdminPassword }),
  });
  const { access_token: adminToken } = await tokenResponse.json();
  const users = await (await fetch(`${keycloak}/admin/realms/helpdesk/users?username=${username}&exact=true`, { headers: { authorization: `Bearer ${adminToken}` } })).json();
  return (await fetch(`${keycloak}/admin/realms/helpdesk/users/${users[0].id}/logout`, { method: 'POST', headers: { authorization: `Bearer ${adminToken}` } })).status;
}

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', timezoneId: zone });
const page = await context.newPage();
const shot = (name) => page.screenshot({ path: path.join(shotsDir, `admin-${name}.png`), fullPage: true });

let watchConsole = true;
const consoleErrors = [];
page.on('console', (message) => {
  if (!watchConsole || message.type() !== 'error') return;
  const url = message.location().url ?? '';
  if (url && !url.startsWith(admin)) return; // la página de Keycloak (su favicon), no el panel
  consoleErrors.push(`${message.text()} @ ${url}`);
});
page.on('pageerror', (error) => { if (watchConsole) consoleErrors.push(`pageerror: ${error.message}`); });
const dialogs = [];
page.on('dialog', async (dialog) => { dialogs.push(dialog.message()); await dialog.dismiss(); });
const sockets = [];
page.on('websocket', (socket) => sockets.push(socket.url()));
let documentRequests = 0;
page.on('request', (request) => { if (request.isNavigationRequest() && request.url().startsWith(admin)) documentRequests++; });

async function fillKeycloakLogin(user) {
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
}
const afterKeycloak = (url) => url.origin === admin && !url.pathname.startsWith('/signin-oidc');
async function login(user, target = '/') {
  await page.goto(`${admin}${target}`);
  await page.waitForURL(/localhost:8080/);
  await fillKeycloakLogin(user);
  await page.waitForURL(afterKeycloak);
}
const rows = page.locator('.mud-table-body tr');
const agentCells = page.locator('.mud-table-body td[data-label="Agente"]');
const statusCells = page.locator('.mud-table-body td[data-label="Estado"]');
async function chooseInSelect(label, option) {
  await page.locator('.mud-select', { hasText: label }).first().click();
  await page.locator('.mud-popover-open .mud-list-item', { hasText: option }).first().click();
}
async function waitForRows() {
  await page.locator('.mud-progress-linear').waitFor({ state: 'detached' });
  await rows.first().waitFor();
}

const created = [];
let lauraJwt;

try {
  await startAdmin();
  lauraJwt = await jwt('laura@helpdesk.local');
  const pedroJwt = await jwt('pedro@helpdesk.local');

  // Datos: un ticket de pedro con conversación (saltos de línea y un <script>), y el id de uno de luis.
  const ticket = (await callApi('POST', '/api/tickets', lauraJwt, { title: 'E2E panel: servidor de correo lento', priority: 'High', assignedTo: 'pedro' })).json;
  created.push(ticket.id);
  await callApi('POST', `/api/tickets/${ticket.id}/comments`, pedroJwt, { body: 'Primer mensaje.\nCon salto de línea.' });
  await callApi('POST', `/api/tickets/${ticket.id}/comments`, pedroJwt, { body: 'Segundo mensaje <script>alert("xss")</script>' });
  const luisTicket = (await callApi('GET', '/api/tickets?assignedTo=luis&pageSize=1', lauraJwt)).json.items[0];

  console.log('--- 1. Sin sesión ---');
  const anonymousHub = await page.request.post(`${admin}/_blazor/negotiate?negotiateVersion=1`, { maxRedirects: 0 });
  check('el WebSocket del circuito también exige sesión (negotiate no entra)', [302, 401].includes(anonymousHub.status()), `status=${anonymousHub.status()} location=${(anonymousHub.headers().location ?? '').slice(0, 45)}`);
  const anonymousDenied = await page.request.get(`${admin}/acceso-denegado`);
  check('/acceso-denegado es pública (200)', anonymousDenied.status() === 200, `status=${anonymousDenied.status()}`);
  await page.goto(`${admin}/`);
  await page.waitForURL(/localhost:8080/);
  check('entrar a / sin sesión lleva directo a Keycloak', true, new URL(page.url()).origin);

  console.log('--- 2. pedro (Agent) ---');
  await fillKeycloakLogin('pedro');
  await page.waitForURL(afterKeycloak);
  await waitForRows();
  check('login y vuelta al panel: 25 filas', (await rows.count()) === 25, `filas=${await rows.count()}`);
  check('el encabezado muestra al usuario', (await page.getByTestId('current-user').innerText()) === 'pedro');
  const agents = new Set(await agentCells.allInnerTexts());
  check('un agente solo ve lo suyo (lo decide la API)', agents.size === 1 && agents.has('pedro'), [...agents].join(','));
  check('un agente no tiene el filtro de agente', (await page.getByLabel('Agente').count()) === 0);
  check('hay un circuito: WebSocket a /_blazor', sockets.some((url) => url.includes('/_blazor')), sockets.at(-1)?.slice(0, 40));

  const cookies = await context.cookies(admin);
  const session = cookies.filter((c) => c.name.startsWith('__Host-helpdesk-admin'));
  const cookieSize = session.reduce((total, c) => total + c.value.length, 0);
  check('cookie HttpOnly, Secure, SameSite=Lax', session.length > 0 && session.every((c) => c.httpOnly && c.secure && c.sameSite === 'Lax'), session.map((c) => c.name).join(','));
  check('la cookie NO lleva tokens: una sola y chica (la del BFF ronda 6 KB)', session.length === 1 && cookieSize < 2500, `${cookieSize} bytes`);
  check('JavaScript no puede leerla', !(await page.evaluate(() => document.cookie)).includes('__Host-helpdesk-admin'));
  const headers = (await page.request.get(`${admin}/`)).headers();
  const csp = headers['content-security-policy'] ?? '';
  check('una sola Content-Security-Policy, con frame-ancestors none', csp.includes("script-src 'self'") && csp.includes("frame-ancestors 'none'") && !csp.includes("frame-ancestors 'self'"), csp);
  await shot('lista-pedro');

  console.log('--- 3. Paginación y filtros ---');
  const documentsBefore = documentRequests;
  await page.getByRole('button', { name: 'Cargar más' }).click();
  await page.waitForFunction(() => document.querySelectorAll('.mud-table-body tr').length > 25);
  check('"Cargar más": 50 filas', (await rows.count()) === 50, `filas=${await rows.count()}`);
  await chooseInSelect('Estado', 'Abierto');
  await page.waitForURL(/estado=Open/);
  // La URL cambia antes de que llegue la respuesta del circuito: se espera a que la lista sea la filtrada.
  await page.waitForFunction(() => {
    const cells = [...document.querySelectorAll('.mud-table-body td[data-label="Estado"]')];
    return cells.length > 0 && cells.every((cell) => cell.innerText.trim() === 'Abierto');
  }, null, { timeout: 15000 });
  const statuses = new Set(await statusCells.allInnerTexts());
  check('filtro por estado: va a la URL y filtra', statuses.size === 1 && statuses.has('Abierto') && (await rows.count()) === 25, `${page.url().replace(admin, '')} ${[...statuses].join(',')}`);
  await page.goBack();
  await page.waitForFunction(() => !location.search.includes('estado'));
  await waitForRows();
  check('"Atrás" quita el filtro', !page.url().includes('estado'), page.url().replace(admin, ''));
  check('paginar y filtrar no recargan la página (todo por el circuito)', documentRequests === documentsBefore, `documentos=${documentRequests - documentsBefore}`);
  await page.goto(`${admin}/tickets?estado=Resolved&prioridad=High`);
  await waitForRows();
  const deepStatuses = new Set(await statusCells.allInnerTexts());
  check('enlace directo con filtros: se aplican', deepStatuses.size === 1 && deepStatuses.has('Resuelto'), [...deepStatuses].join(','));
  check('y los controles los muestran', (await page.locator('.mud-select', { hasText: 'Estado' }).first().locator('input').first().inputValue()) === 'Resuelto');
  await page.goto(`${admin}/tickets?estado=NoExiste`);
  await waitForRows();
  check('un valor inválido en la URL se ignora', (await rows.count()) === 25);

  console.log('--- 4. Detalle ---');
  await page.goto(`${admin}/tickets/${ticket.id}`);
  await page.getByRole('heading', { name: 'servidor de correo lento' }).waitFor();
  await page.locator('.comment').first().waitFor();
  check('el detalle muestra el ticket y su conversación', (await page.locator('.comment').count()) === 2);
  check('la conversación va en orden de lectura y respeta los saltos de línea', (await page.locator('.comment-body').first().innerText()) === 'Primer mensaje.\nCon salto de línea.', JSON.stringify(await page.locator('.comment-body').first().innerText()));
  check('el <script> se muestra como texto y no se ejecuta', dialogs.length === 0 && (await page.locator('.comment-body').nth(1).innerText()).includes('<script>') && (await page.locator('.comment-body script').count()) === 0);
  const expectedTime = new Date(ticket.createdAt).toLocaleTimeString('es-CL', { timeZone: zone, hour: '2-digit', minute: '2-digit', hour12: false });
  const shownDate = await page.locator('.ticket-facts tr', { hasText: 'Creado' }).locator('time').innerText();
  check(`la fecha sale en la zona del navegador (${zone}), no en la del servidor`, shownDate.includes(expectedTime), `${shownDate} (esperaba ${expectedTime})`);
  check('título de la pestaña con el código', (await page.title()) === `${ticket.code} · HelpDesk`, await page.title());
  await shot('detalle');
  await page.goto(`${admin}/tickets/999999999`);
  await page.getByRole('heading', { name: 'No encontramos ese ticket' }).waitFor();
  check('404 de la API: "No encontramos ese ticket"', true);
  await page.goto(`${admin}/tickets/${luisTicket.id}`);
  await page.getByRole('heading', { name: 'Este ticket no está asignado a ti' }).waitFor();
  check('403 de la API (ticket de luis): el panel lo explica', true);

  console.log('--- 5. Errores: la barrera mantiene vivo el circuito ---');
  await page.goto(`${admin}/tickets?agente=luis`);
  await page.getByTestId('api-error').waitFor();
  check('pedro falsea ?agente=luis: la API responde 403 y se muestra el error', (await page.getByTestId('api-error').innerText()).includes('Sin permiso'), (await page.getByTestId('api-error').innerText()).replace(/\s+/g, ' '));
  check('el circuito sigue vivo (no aparece el aviso de error fatal)', !(await page.locator('#blazor-error-ui').isVisible()));
  await page.getByRole('link', { name: 'Tickets', exact: true }).click();
  await page.waitForFunction(() => !location.search.includes('agente'));
  await waitForRows();
  check('al navegar, la barrera se recupera sola', (await page.getByTestId('api-error').count()) === 0 && (await rows.count()) === 25);

  console.log('--- 6. Renovación del token con el circuito abierto ---');
  const refreshesBefore = refreshCount();
  const documentsBeforeRefresh = documentRequests;
  await sleep(3000);
  await page.getByRole('button', { name: 'Cargar más' }).click();
  await page.waitForFunction(() => document.querySelectorAll('.mud-table-body tr').length > 25);
  check('token "por vencer": se renovó y la llamada funcionó, sin recargar', refreshCount() > refreshesBefore && documentRequests === documentsBeforeRefresh, `renovaciones=${refreshCount() - refreshesBefore} documentos=${documentRequests - documentsBeforeRefresh}`);
  await sleep(3000);
  const refreshesBeforeDetail = refreshCount();
  await page.locator('.mud-table-body a').first().click();
  await page.locator('.ticket-facts').waitFor();
  check('dos llamadas en paralelo (ticket y conversación): una sola renovación (el candado)', refreshCount() - refreshesBeforeDetail === 1, `renovaciones=${refreshCount() - refreshesBeforeDetail}`);
  check('nada de esto recargó la página', documentRequests === documentsBeforeRefresh, `documentos=${documentRequests - documentsBeforeRefresh}`);

  console.log('--- 7. Antiforgery en el logout ---');
  const forgedLogout = await page.request.post(`${admin}/auth/logout`, { maxRedirects: 0 });
  check('POST /auth/logout sin token antiforgery: 400, no cierra la sesión', forgedLogout.status() === 400, `status=${forgedLogout.status()}`);
  check('el panel sigue funcionando', (await page.getByTestId('current-user').innerText()) === 'pedro');

  check('sin errores en la consola (incluye violaciones de CSP)', consoleErrors.length === 0, consoleErrors.join(' | '));

  console.log('--- 8. Keycloak revoca la sesión mientras el circuito está abierto ---');
  watchConsole = false; // el navegador registra errores al cortarse el circuito: son esperados de aquí en adelante
  check('un administrador cierra las sesiones de pedro en Keycloak', (await revokeKeycloakSessions('pedro')) === 204);
  await sleep(3000);
  await page.getByRole('link', { name: 'Tickets', exact: true }).click();
  await page.waitForURL(/localhost:8080/, { timeout: 15000 });
  await page.locator('#username').waitFor();
  check('la siguiente llamada falla al renovar y lleva a la pantalla de login', true, new URL(page.url()).origin);
  await fillKeycloakLogin('pedro');
  await page.waitForURL(afterKeycloak);
  await waitForRows();
  check('al volver a entrar todo funciona', (await page.getByTestId('current-user').innerText()) === 'pedro', page.url().replace(admin, ''));

  console.log('--- 9. Cerrar sesión ---');
  await page.getByRole('button', { name: 'Cerrar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  await page.locator('#username').waitFor();
  check('termina en el login de Keycloak, sin pedir confirmación (id_token_hint)', true, new URL(page.url()).pathname.slice(0, 40));
  check('la cookie ya no está', (await context.cookies(admin)).filter((c) => c.name.startsWith('__Host-helpdesk-admin')).length === 0);

  console.log('--- 10. camila (Customer): Static SSR ---');
  const socketsBefore = sockets.length;
  await fillKeycloakLogin('camila');
  await page.waitForURL(/acceso-denegado/);
  await page.getByRole('heading', { name: 'Este panel es para el equipo de soporte' }).waitFor();
  check('un cliente inicia sesión pero queda en /acceso-denegado', (await page.locator('.static-page').innerText()).includes('camila'), page.url().replace(admin, '').slice(0, 40));
  await sleep(1500);
  check('Static SSR: esta página no abre ningún circuito', sockets.length === socketsBefore, `websockets=${sockets.length - socketsBefore}`);
  const customerHub = await page.request.post(`${admin}/_blazor/negotiate?negotiateVersion=1`, { maxRedirects: 0 });
  check('y su cuenta no puede abrir el WebSocket a mano', customerHub.status() !== 200, `status=${customerHub.status()}`);
  await shot('acceso-denegado');
  await page.getByRole('button', { name: 'Cerrar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  check('cierra sesión desde la página estática (antiforgery en SSR)', true);

  console.log('--- 11. laura (Supervisor) ---');
  await page.locator('#username').waitFor();
  await fillKeycloakLogin('laura');
  await page.waitForURL(afterKeycloak);
  await waitForRows();
  check('supervisión ve el trabajo de varios agentes', new Set(await agentCells.allInnerTexts()).size >= 2, [...new Set(await agentCells.allInnerTexts())].join(','));
  await page.getByLabel('Agente').fill('luis');
  await page.waitForURL(/agente=luis/);
  await waitForRows();
  const lauraAgents = new Set(await agentCells.allInnerTexts());
  check('filtro por agente: espera a que termine de escribir (debounce) y filtra', lauraAgents.size === 1 && lauraAgents.has('luis'), page.url().replace(admin, ''));
  await shot('lista-laura');
  await page.goto(`${admin}/tickets/${luisTicket.id}`);
  await page.locator('.ticket-facts').waitFor();
  check('el ticket de luis que pedro no podía ver', true, await page.locator('h1').innerText());
  await page.goto(`${admin}/no-existe`);
  await page.getByRole('heading', { name: 'No encontramos esa página' }).waitFor();
  check('ruta desconocida: página 404', true);

  console.log('--- 12. Se reinicia el panel (los tokens están en memoria) ---');
  await page.goto(`${admin}/tickets`);
  await waitForRows();
  await stopAdmin();
  await page.locator('#components-reconnect-modal').waitFor({ state: 'visible', timeout: 15000 });
  check('al cortarse el circuito aparece el aviso de reconexión, en español', (await page.locator('#components-reconnect-modal').innerText()).length > 0, (await page.locator('#components-reconnect-modal').innerText()).replace(/\s+/g, ' ').slice(0, 60));
  await startAdmin();
  await page.goto(`${admin}/tickets`);
  await page.waitForURL(afterKeycloak);
  await waitForRows();
  check('la cookie sigue válida pero sin tokens: Keycloak los entrega de nuevo sin pedir contraseña', (await page.getByTestId('current-user').innerText()) === 'laura');
} catch (error) {
  fail++;
  console.log(`FAIL  excepción: ${error.message.split('\n')[0]}`);
  await shot('error').catch(() => {});
  console.log(`      url=${page.url()}`);
  console.log(adminLog.split('\n').filter((line) => /fail:|error|Exception/i.test(line)).slice(-10).join('\n'));
} finally {
  await browser.close();
  await stopAdmin();
  if (lauraJwt) {
    const deleted = [];
    for (const id of created) deleted.push((await callApi('DELETE', `/api/tickets/${id}`, lauraJwt)).status);
    console.log(`--- Limpieza: ${created.length} tickets borrados (status ${[...new Set(deleted)].join(',')}) ---`);
  }
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
