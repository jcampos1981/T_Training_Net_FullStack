// Prueba de punta a punta de los avisos en vivo del portal (Fase 7, Bloque 1) en Edge headless.
// Uso: tests/e2e/run.ps1 -Suite portal-live  (por dentro: node portal-live.mjs <deps> <capturas> <repo> <url-del-portal> [largo]    Contraseña en HELPDESK_DEV_PASSWORD.)
//   La API debe estar corriendo (y Vite, si la URL es la suya). El script levanta y detiene el BFF.
//   "largo": además espera a que venza el token de Keycloak (unos 5 minutos).
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo, portal, long] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const Ws = createRequire(path.join(repo, 'tests/e2e/package.json'))('ws');
const password = process.env.HELPDESK_DEV_PASSWORD;
const api = 'http://localhost:5159';
const bff = 'https://localhost:7200';
const servedByBff = portal.startsWith(bff);
const mode = servedByBff ? 'bff' : 'vite';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

let bffProcess;
let bffLog = '';
async function startBff() {
  bffLog = '';
  bffProcess = spawn(path.join(repo, 'src/HelpDesk.Bff/bin/Debug/net10.0/HelpDesk.Bff.exe'), [], {
    cwd: path.join(repo, 'src/HelpDesk.Bff'),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: bff },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  bffProcess.stdout.on('data', (chunk) => { bffLog += chunk; });
  bffProcess.stderr.on('data', (chunk) => { bffLog += chunk; });
  for (let i = 0; i < 120 && !bffLog.includes('Application started'); i++) await sleep(250);
  if (!bffLog.includes('Application started')) throw new Error(`El BFF no arrancó:\n${bffLog}`);
}
function stopBff() {
  bffProcess?.kill();
  bffProcess = undefined;
}

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

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1200, height: 800 } });
const page = await context.newPage();
const portalOrigin = new URL(portal).origin;
const shot = (name) => page.screenshot({ path: path.join(shotsDir, `f7-portal-${mode}-${name}.png`) });

// Con el BFF abajo, el navegador registra errores de red; son lo que la prueba provoca. El resto cuenta.
let networkFailuresExpected = false;
const consoleErrors = [];
page.on('console', (message) => {
  if (message.type() !== 'error') return;
  const text = message.text();
  const url = message.location().url ?? '';
  if (url && !url.startsWith(portalOrigin)) return;
  if (networkFailuresExpected && /ERR_CONNECTION_REFUSED|WebSocket connection|status of (401|502|503|500)|Failed to load resource/.test(text)) return;
  if (/status of 4\d\d/.test(text) && /\/(bff\/user|api\/portal\/tickets[^?]*)$/.test(url)) return;
  consoleErrors.push(`${text} @ ${url}`);
});
page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));
const hubSockets = [];
page.on('websocket', (socket) => { if (socket.url().includes('/hubs/')) hubSockets.push(socket); });
const allUrls = [];
page.on('request', (request) => allUrls.push(request.url()));
let documentRequests = 0;
page.on('request', (request) => { if (request.isNavigationRequest() && request.url().startsWith(portalOrigin)) documentRequests++; });

// Siempre en el BFF: Keycloak solo acepta el puerto 5173 de Vite, y ese puerto puede estar ocupado por otro proyecto. La
// cookie vale para todo "localhost", sin importar el puerto, así que después se puede abrir Vite en cualquier otro.
async function login(target, user) {
  await target.goto(bff);
  await target.getByRole('link', { name: 'Iniciar sesión' }).click();
  await target.waitForURL(/localhost:8080/);
  await target.fill('#username', user);
  await target.fill('#password', password);
  await target.click('#kc-login');
  await target.waitForURL((url) => url.origin === bff && !url.pathname.startsWith('/signin-oidc'));
  if (!servedByBff) await target.goto(portal);
}

const notice = (text) => page.locator('.notice', { hasText: text });
const offline = page.locator('.live-offline');
const comments = page.locator('.comment-body');
const has = async (locator, ms = 5000) => locator.first().waitFor({ timeout: ms }).then(() => true).catch(() => false);
const gone = async (locator, ms = 5000) => locator.first().waitFor({ state: 'detached', timeout: ms }).then(() => true).catch(() => false);
const liveSockets = () => hubSockets.filter((socket) => !socket.isClosed());

const created = [];
const tag = `live-${Date.now()}`;
let lauraJwt;

try {
  await startBff();
  lauraJwt = await jwt('laura@helpdesk.local');
  const camilaJwt = await jwt('camila@acme.local');
  const updateAs = async (id, changes) => {
    const current = (await callApi('GET', `/api/tickets/${id}`, lauraJwt)).json;
    return callApi('PUT', `/api/tickets/${id}`, lauraJwt, { title: current.title, priority: current.priority, status: current.status, assignedTo: current.assignedTo, version: current.version, ...changes });
  };

  console.log(`--- [${mode}] 1. Conexión ---`);
  await login(page, 'camila');
  const loginAt = Date.now();
  await page.locator('table.tickets').waitFor();
  const opened = await (async () => { for (let i = 0; i < 40 && liveSockets().length === 0; i++) await sleep(100); return liveSockets().length; })();
  check('Al entrar se abre el WebSocket del hub', opened === 1, hubSockets[0]?.url().replace(/id=.*/, 'id=...'));
  check('El WebSocket va al mismo origen de la página', hubSockets[0]?.url().startsWith(portalOrigin.replace('http', 'ws')));
  const docsAfterLogin = documentRequests;

  console.log(`--- [${mode}] 2. Un cambio propio desde otro lugar ---`);
  const opened1 = await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `${tag} impresora`, priority: 'Medium', description: 'No imprime.' });
  created.push(opened1.json.id);
  const ticketId = opened1.json.id;
  const code = opened1.json.code;
  check('La lista muestra el ticket nuevo sin recargar', await has(page.locator('table.tickets tbody tr', { hasText: `${tag} impresora` })));
  await sleep(300);
  check('Un cambio propio no genera aviso', await page.locator('.notice').count() === 0);

  console.log(`--- [${mode}] 3. Soporte responde mientras el cliente mira el ticket ---`);
  await page.locator('table.tickets tbody tr', { hasText: `${tag} impresora` }).getByRole('link').first().click();
  await page.waitForURL(`**/tickets/${ticketId}`);
  await has(comments);
  await page.fill('#reply', 'Borrador a medio escribir');
  const respondedAt = Date.now();
  await callApi('POST', `/api/tickets/${ticketId}/comments`, lauraJwt, { body: 'Estamos revisando tu impresora.' });
  check('La respuesta aparece en la conversación sin recargar', await has(comments.filter({ hasText: 'Estamos revisando tu impresora.' })), `${Date.now() - respondedAt} ms`);
  check('Aparece el aviso de soporte', await has(notice(`Soporte respondió en ${code}`)));
  check('El borrador de respuesta sigue intacto', await page.inputValue('#reply') === 'Borrador a medio escribir');
  check('No hubo navegación', documentRequests === docsAfterLogin, `${documentRequests - docsAfterLogin}`);
  await shot('aviso');
  await page.locator('.notice-close').first().click();
  check('El aviso se puede cerrar', await gone(notice(`Soporte respondió en ${code}`)));
  await page.fill('#reply', '');

  console.log(`--- [${mode}] 4. Cambio de estado ---`);
  await updateAs(ticketId, { status: 'InProgress' });
  check('El estado cambia en pantalla', await has(page.locator('.ticket-facts .status', { hasText: 'En atención' })));
  check('El aviso dice el estado nuevo', await has(notice('Estado: En atención')));

  console.log(`--- [${mode}] 5. Lo que no le incumbe al cliente ---`);
  await page.locator('.notice-close').first().click();
  await updateAs(ticketId, { assignedTo: 'pedro' });
  await sleep(800);
  check('Asignar un agente no genera aviso', await page.locator('.notice').count() === 0);
  const bodyText = await page.locator('body').innerText();
  check('El nombre del agente no aparece en el portal', !bodyText.includes('pedro'));

  console.log(`--- [${mode}] 6. Otra pestaña del mismo cliente ---`);
  const secondTab = await context.newPage();
  await secondTab.goto(`${portal}/tickets/${ticketId}`);
  await secondTab.fill('#reply', 'Lo escribí en la otra pestaña');
  await secondTab.getByRole('button', { name: /Enviar|Responder/ }).click();
  check('La otra pestaña ve la respuesta sin recargar', await has(comments.filter({ hasText: 'Lo escribí en la otra pestaña' })));
  await sleep(300);
  check('Y sin aviso: fue el mismo cliente', await page.locator('.notice').count() === 0);
  await secondTab.close();

  console.log(`--- [${mode}] 7. Desde la lista, el aviso lleva al ticket ---`);
  await page.getByRole('link', { name: '← Mis tickets' }).click();
  await page.locator('table.tickets').waitFor();
  await callApi('POST', `/api/tickets/${ticketId}/comments`, lauraJwt, { body: 'Otra respuesta.' });
  await has(notice(`Soporte respondió en ${code}`));
  await notice(`Soporte respondió en ${code}`).getByRole('link', { name: 'Ver' }).click();
  await page.waitForURL(`**/tickets/${ticketId}`);
  check('"Ver" navega al ticket y cierra el aviso', await has(comments.filter({ hasText: 'Otra respuesta.' })) && await page.locator('.notice').count() === 0);

  console.log(`--- [${mode}] 8. Ráfaga y duración de los avisos ---`);
  await Promise.all([1, 2, 3, 4, 5].map((n) => callApi('POST', `/api/tickets/${ticketId}/comments`, lauraJwt, { body: `Ráfaga ${n}` })));
  await has(comments.filter({ hasText: 'Ráfaga 5' }));
  await sleep(300);
  const shown = await page.locator('.notice').count();
  check('Una ráfaga muestra como máximo 3 avisos', shown > 0 && shown <= 3, `${shown}`);
  check('La conversación tiene las 5 respuestas de la ráfaga', await comments.filter({ hasText: 'Ráfaga' }).count() === 5);
  await page.locator('.notice').first().waitFor({ state: 'detached', timeout: 13_000 }).catch(() => {});
  check('Los avisos desaparecen solos', await gone(page.locator('.notice'), 13_000));

  console.log(`--- [${mode}] 9. Cierre y eliminación mientras se mira ---`);
  await updateAs(ticketId, { status: 'Closed' });
  check('Cerrado: el formulario de respuesta desaparece', await gone(page.locator('#reply')));
  check('Cerrado: se muestra el aviso de ticket cerrado', await has(page.getByText('Este ticket está cerrado')));
  await sleep(200);
  await callApi('DELETE', `/api/tickets/${ticketId}`, lauraJwt);
  created.splice(created.indexOf(ticketId), 1);
  check('Eliminado: la pantalla lo dice', await has(page.getByRole('heading', { name: 'No encontramos ese ticket' })));
  check('Eliminado: el aviso no ofrece un enlace roto', await has(notice('eliminó')) && await notice('eliminó').getByRole('link').count() === 0);

  console.log(`--- [${mode}] 10. Contenido hostil ---`);
  const hostile = await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `<img src=x onerror="window.__xss=1">${tag}`, priority: 'Low', description: 'Descripción de prueba.' });
  check('El ticket con HTML en el título se creó', hostile.status === 201, `${hostile.status}`);
  created.push(hostile.json.id);
  await page.getByRole('link', { name: 'Volver a mis tickets' }).click();
  await has(page.locator('table.tickets tbody tr', { hasText: tag }));
  await callApi('POST', `/api/tickets/${hostile.json.id}/comments`, lauraJwt, { body: '<script>window.__xss=2</script>' });
  await sleep(500);
  check('Un título con HTML se muestra como texto', await page.locator('table.tickets img').count() === 0);
  check('Ningún script inyectado se ejecutó', await page.evaluate(() => window.__xss) === undefined);

  console.log(`--- [${mode}] 11. Nada de tokens en el navegador ---`);
  const leaked = [...allUrls, ...hubSockets.map((socket) => socket.url())].filter((url) => /access_token|refresh_token/.test(url));
  check('Ninguna URL lleva un token (ni la del WebSocket)', leaked.length === 0, leaked[0] ?? '');
  const storage = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage }, cookie: document.cookie }));
  check('Sin tokens en storage ni en cookies legibles', !/eyJ/.test(storage));

  if (servedByBff) {
    console.log(`--- [${mode}] 12. Cross-Site WebSocket Hijacking y CSRF ---`);
    const cookie = (await context.cookies(bff)).map((entry) => `${entry.name}=${entry.value}`).join('; ');
    const openSocket = (headers) => new Promise((resolve) => {
      const socket = new Ws(`${bff.replace('https', 'wss')}/hubs/tickets`, { headers, rejectUnauthorized: false });
      socket.on('open', () => { socket.close(); resolve(101); });
      socket.on('unexpected-response', (_, response) => resolve(response.statusCode));
      socket.on('error', (error) => resolve(error.message));
    });
    check('WebSocket con la cookie desde otro sitio: rechazado', await openSocket({ cookie, origin: 'https://atacante.example' }) === 400);
    check('WebSocket con la cookie desde otro puerto del mismo sitio: rechazado', await openSocket({ cookie, origin: 'https://localhost:9999' }) === 400);
    check('WebSocket con la cookie y sin Origin: rechazado', await openSocket({ cookie }) === 400);
    check('WebSocket del propio origen, sin cookie: 401', await openSocket({ origin: bff }) === 401);
    check('WebSocket del propio origen, con cookie: aceptado', await openSocket({ cookie, origin: bff }) === 101);
    const negotiate = (headers) => page.request.post(`${bff}/hubs/tickets/negotiate?negotiateVersion=1`, { headers });
    check('negotiate sin X-CSRF: rechazado', (await negotiate({ origin: '' })).status() === 400);
    check('negotiate desde otro origen, sin X-CSRF: rechazado', (await negotiate({ origin: 'https://atacante.example' })).status() === 400);
    const accepted = await negotiate({ 'x-csrf': '1' });
    check('negotiate con X-CSRF: aceptado, y sin tokens en la respuesta', accepted.status() === 200 && !/eyJ/.test(await accepted.text()), `${accepted.status()}`);
    const headers = (await page.request.get(`${portal}/`)).headers();
    check('La CSP no se relajó para el WebSocket', headers['content-security-policy']?.includes("connect-src 'self'"), headers['content-security-policy']);

    console.log(`--- [${mode}] 13. Corte de conexión: lo perdido se recupera ---`);
    const liveTicket = await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `${tag} corte`, priority: 'Low', description: 'Descripción de prueba.' });
    check('El ticket para el corte se creó', liveTicket.status === 201, `${liveTicket.status}`);
    created.push(liveTicket.json.id);
    hubSockets.length = 0; // al navegar, Playwright no informa el cierre de los sockets de la página anterior
    await page.goto(`${portal}/tickets/${liveTicket.json.id}`);
    await has(comments);
    networkFailuresExpected = true;
    stopBff();
    check('Sin conexión: se avisa en pantalla', await has(offline, 15_000));
    await shot('sin-conexion');
    const missed = await callApi('POST', `/api/tickets/${liveTicket.json.id}/comments`, lauraJwt, { body: 'Enviado durante el corte.' });
    await sleep(500);
    check('Durante el corte, ese comentario no aparece (el aviso se perdió)', missed.status === 201 && await comments.filter({ hasText: 'Enviado durante el corte.' }).count() === 0);
    await startBff();
    check('Al volver, el aviso de "sin conexión" desaparece solo', await gone(offline, 40_000));
    check('Al volver, se recupera lo que se perdió, sin recargar', await has(comments.filter({ hasText: 'Enviado durante el corte.' }), 8000));
    networkFailuresExpected = false;
    const livePost = await callApi('POST', `/api/tickets/${liveTicket.json.id}/comments`, lauraJwt, { body: 'Después del corte.' });
    check('Los avisos en vivo siguen funcionando', livePost.status === 201 && await has(comments.filter({ hasText: 'Después del corte.' })));

    if (long) {
      console.log(`--- [${mode}] 14. El token de Keycloak vence con la pantalla abierta ---`);
      const socketsBefore = hubSockets.length;
      let offlineSeen = false;
      const deadline = loginAt + 6 * 60_000;
      const expectedNew = liveSockets()[0];
      while (Date.now() < deadline && !expectedNew?.isClosed()) {
        offlineSeen ||= await offline.count() > 0;
        await sleep(500);
      }
      const closedAfter = Math.round((Date.now() - loginAt) / 1000);
      check('La API cierra el WebSocket cuando vence el token', expectedNew?.isClosed() === true, `a los ${closedAfter} s del login`);
      for (let i = 0; i < 40 && liveSockets().length === 0; i++) { offlineSeen ||= await offline.count() > 0; await sleep(250); }
      check('El portal se reconecta solo', liveSockets().length === 1 && hubSockets.length > socketsBefore, `${hubSockets.length - socketsBefore} conexión nueva`);
      check('El usuario no vio ningún aviso de corte', !offlineSeen);
      const afterRenew = await callApi('POST', `/api/tickets/${liveTicket.json.id}/comments`, lauraJwt, { body: 'Después de renovar el token.' });
      check('Los avisos siguen llegando con el token nuevo', afterRenew.status === 201 && await has(comments.filter({ hasText: 'Después de renovar el token.' })));
    }

    console.log(`--- [${mode}] 15. La sesión termina con la pantalla abierta ---`);
    networkFailuresExpected = true;
    await context.clearCookies();
    stopBff();
    await startBff();
    check('Sin sesión, deja de insistir y ofrece iniciar sesión', await has(page.getByRole('link', { name: 'Iniciar sesión' }), 40_000));
    await sleep(500);
    check('Y cierra su conexión', liveSockets().length === 0, `${hubSockets.map((socket) => (socket.isClosed() ? 'cerrado' : 'abierto')).join(',')}`);
    networkFailuresExpected = false;

    console.log(`--- [${mode}] 16. Una cuenta del equipo en el portal ---`);
    const staff = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
    const staffPage = await staff.newPage();
    const staffSockets = [];
    staffPage.on('websocket', (socket) => { if (socket.url().includes('/hubs/')) staffSockets.push(socket); });
    await login(staffPage, 'pedro');
    await staffPage.getByText('Esta cuenta no es de cliente').waitFor();
    await sleep(500);
    check('No abre la conexión de avisos', staffSockets.length === 0);
    await staff.close();
  }

  check('Sin errores de consola ni violaciones de CSP', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '));
} finally {
  stopBff();
  await browser.close();
  const cleaner = lauraJwt ?? await jwt('laura@helpdesk.local');
  for (const id of created) await callApi('DELETE', `/api/tickets/${id}`, cleaner).catch(() => {});
}

console.log(`\nTOTAL [${mode}]: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
