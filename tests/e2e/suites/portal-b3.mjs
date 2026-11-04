// Prueba de punta a punta del portal (Fase 6, Bloque 3) en Edge headless.
// Uso: tests/e2e/run.ps1 -Suite portal-b3  (por dentro: node portal-b3.mjs <carpeta-deps> <carpeta-capturas> <url-del-portal>   (contraseña en HELPDESK_DEV_PASSWORD))
//   https://localhost:7200 → el build servido por el BFF (revisa también headers de caché y CSP)
//   http://localhost:5173  → Vite en desarrollo
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';

const [depsDir, shotsDir, portalArg] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const portal = portalArg ?? 'http://localhost:5173';
const servedByBff = portal.startsWith('https://localhost:7200');
const mode = servedByBff ? 'bff' : 'vite';
const api = 'http://localhost:5159';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

// Llamadas directas a la API (fuera del navegador) para preparar datos y limpiar: JWT propios de la API.
async function jwt(email) {
  const response = await fetch(`${api}/api/token`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }),
  });
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
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
const page = await context.newPage();
const portalOrigin = new URL(portal).origin;
const shot = (name) => page.screenshot({ path: path.join(shotsDir, `b3-${mode}-${name}.png`), fullPage: true });

// Errores de consola: el navegador registra toda respuesta 4xx. Los 4xx de /bff/user (sin sesión) y de
// /api/portal/... (400, 404 y 409 que la prueba provoca a propósito) son esperados; cualquier otro error cuenta,
// incluidas las violaciones de la Content-Security-Policy ("Refused to ...").
const consoleErrors = [];
page.on('console', (message) => {
  if (message.type() !== 'error') return;
  const url = message.location().url ?? '';
  const expected4xx = /status of 4\d\d/.test(message.text()) && /\/(bff\/user|api\/portal\/tickets[^?]*)$/.test(url);
  if (url.startsWith(portalOrigin) && expected4xx) return;
  if (url && !url.startsWith(portalOrigin)) return; // la página de Keycloak (su favicon), no la app
  consoleErrors.push(`${message.text()} @ ${url}`);
});
page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));
const dialogs = [];
page.on('dialog', async (dialog) => { dialogs.push(dialog.message()); await dialog.dismiss(); });
const requests = [];
page.on('request', (request) => requests.push(`${request.method()} ${new URL(request.url()).pathname}`));

async function login(user) {
  await page.getByRole('link', { name: 'Iniciar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL((url) => url.origin === portalOrigin && !url.pathname.startsWith('/signin-oidc'));
}

const rows = page.locator('table.tickets tbody tr');
const lauraJwt = await jwt('laura@helpdesk.local');
const created = [];

try {
  const diegoTicket = (await callApi('GET', '/api/portal/tickets', await jwt('diego@acme.local'))).json.items[0].id;

  console.log(`--- [${mode}] 1. Entrar directo a /tickets/new sin sesión ---`);
  const first = await page.goto(`${portal}/tickets/new`);
  if (servedByBff) {
    const headers = first.headers();
    check('BFF: /tickets/new (ruta de React) responde index.html', first.status() === 200 && (await first.text()).includes('<div id="root">'), `status=${first.status()}`);
    check('index.html: Cache-Control no-cache', headers['cache-control'] === 'no-cache', headers['cache-control']);
    check('Content-Security-Policy en la página', (headers['content-security-policy'] ?? '').includes("script-src 'self'"), headers['content-security-policy']);
    check('X-Content-Type-Options: nosniff', headers['x-content-type-options'] === 'nosniff', headers['x-content-type-options']);
    const script = await page.locator('script[type="module"]').getAttribute('src');
    const asset = await page.request.get(`${portal}${script}`);
    check('/assets con hash: caché de un año, immutable', asset.headers()['cache-control'] === 'public, max-age=31536000, immutable', `${script} → ${asset.headers()['cache-control']}`);
    const bffMissing = await page.request.get(`${portal}/bff/no-existe`);
    check('/bff/no-existe: 404 con ProblemDetails, no index.html', bffMissing.status() === 404 && !(await bffMissing.text()).includes('id="root"'), `status=${bffMissing.status()} ${bffMissing.headers()['content-type']}`);
  }
  await page.getByRole('heading', { name: 'Soporte para clientes' }).waitFor();
  check('sin sesión: bienvenida', true);

  await login('camila');
  await page.getByRole('heading', { name: 'Nuevo ticket' }).waitFor();
  check('el login vuelve a /tickets/new (returnUrl)', page.url() === `${portal}/tickets/new`, page.url());

  console.log(`--- [${mode}] 2. Lista inicial (queda en caché) ---`);
  await page.getByRole('link', { name: '← Mis tickets' }).click();
  await rows.first().waitFor();
  const initialRows = await rows.count();
  check('camila: sus tickets del seeder', initialRows === 3, `filas=${initialRows}`);

  console.log(`--- [${mode}] 3. Formulario: la validación la hace la API ---`);
  await page.getByRole('link', { name: 'Nuevo ticket' }).click();
  await page.getByRole('heading', { name: 'Nuevo ticket' }).waitFor();
  const options = (await page.locator('#priority option').allTextContents()).join(',');
  check('el select ofrece Baja, Media y Alta (sin Crítica), Media por defecto', options === 'Baja,Media,Alta' && (await page.inputValue('#priority')) === 'Medium', options);
  await page.getByRole('button', { name: 'Abrir ticket' }).click();
  await page.getByText('Revisa los campos marcados.').waitFor();
  const titleErrors = await page.locator('#title-errors').innerText();
  const descriptionErrors = await page.locator('#description-errors').innerText();
  check('vacío: los mensajes de [Required] junto a cada campo', titleErrors.includes('El título es obligatorio.') && descriptionErrors.includes('La descripción es obligatoria.'), `${titleErrors} | ${descriptionErrors}`);
  check('aria-invalid y aria-describedby en el campo con error',
    (await page.getAttribute('#title', 'aria-invalid')) === 'true' && (await page.getAttribute('#title', 'aria-describedby')) === 'title-errors');
  await page.fill('#title', 'abc');
  await page.fill('#description', 'corta');
  await page.getByRole('button', { name: 'Abrir ticket' }).click();
  await page.locator('#title-errors', { hasText: 'entre 5 y 120' }).waitFor();
  check('muy cortos: mensajes de [StringLength], y lo escrito se conserva', (await page.inputValue('#title')) === 'abc', await page.locator('#description-errors').innerText());
  await shot('form-errores');

  console.log(`--- [${mode}] 4. Abrir el ticket ---`);
  const title = `E2E ${mode}: la impresora del piso 3 no imprime`;
  await page.fill('#title', title);
  await page.fill('#description', 'Desde el lunes la impresora no imprime.\nYa la reinicié dos veces.');
  await page.selectOption('#priority', 'High');
  const requestsBefore = requests.length;
  await page.getByRole('button', { name: 'Abrir ticket' }).click();
  await page.waitForURL(/\/tickets\/\d+$/);
  const newId = Number(new URL(page.url()).pathname.split('/').pop());
  created.push(newId);
  await page.getByRole('heading', { name: title }).waitFor();
  const facts = await page.locator('.ticket-facts').innerText();
  check('detalle: Abierto, prioridad Alta', facts.includes('Abierto') && facts.includes('Alta'), facts.replace(/\s+/g, ' '));
  await page.locator('.comment').first().waitFor();
  const description = await page.locator('.comment-body').first().innerText();
  check('la descripción es el primer mensaje, con su salto de línea', description === 'Desde el lunes la impresora no imprime.\nYa la reinicié dos veces.', JSON.stringify(description));
  const after = requests.slice(requestsBefore);
  // El aviso en vivo de este mismo ticket puede llegar con la pantalla ya abierta, y entonces se vuelve a pedir una vez.
  // Lo que importa es que la pantalla no lo necesitó para mostrarse: sin caché, el GET del ticket saldría antes que el otro.
  const ticketRequest = `GET /api/portal/tickets/${newId}`;
  const commentsRequest = `GET /api/portal/tickets/${newId}/comments`;
  check('el detalle salió de la caché (setQueryData): el GET del ticket no sale al abrir la pantalla',
    after.includes(commentsRequest)
      && after.filter((request) => request === ticketRequest).length <= 1
      && (!after.includes(ticketRequest) || after.indexOf(ticketRequest) > after.indexOf(commentsRequest)), after.join(' | '));
  const code = await page.locator('h1 .code').innerText();
  check('título de la pestaña con el código (React 19 <title>)', (await page.title()) === `${code} · HelpDesk`, await page.title());

  console.log(`--- [${mode}] 5. Responder ---`);
  await page.getByRole('button', { name: 'Responder' }).click();
  await page.locator('#reply-errors', { hasText: 'El comentario es obligatorio.' }).waitFor();
  check('respuesta vacía: mensaje de la API', true);
  const reply = 'Es una HP LaserJet. <script>alert("xss")</script>';
  await page.fill('#reply', reply);
  await page.getByRole('button', { name: 'Responder' }).click();
  await page.locator('.comment').nth(1).waitFor();
  check('la respuesta aparece al final de la conversación', (await page.locator('.comment').count()) === 2 && (await page.locator('.comment-body').nth(1).innerText()) === reply);
  check('el <script> se muestra como texto y no se ejecuta', dialogs.length === 0 && (await page.locator('.comment-body script').count()) === 0, `dialogs=${dialogs.length}`);
  check('el campo queda vacío después de enviar', (await page.inputValue('#reply')) === '');

  console.log(`--- [${mode}] 6. La lista se actualiza (invalidación) ---`);
  await page.getByRole('link', { name: '← Mis tickets' }).click();
  await page.getByRole('link', { name: title }).waitFor();
  check('volver a la lista: el ticket nuevo ya está (la caché se invalidó)', (await rows.count()) === initialRows + 1, `filas=${await rows.count()}`);

  console.log(`--- [${mode}] 7. Recargar en el detalle y cierre por soporte ---`);
  await page.getByRole('link', { name: title }).click();
  await page.getByRole('heading', { name: title }).waitFor();
  await page.reload();
  await page.getByRole('heading', { name: title }).waitFor();
  check('recargar en /tickets/:id muestra el detalle', true, page.url());
  await page.locator('.comment').nth(1).waitFor();
  await shot('detalle');

  // Directo a la base, sin aviso en vivo: con aviso, el formulario desaparece antes de que camila envíe (lo cubre
  // portal-live-e2e). El 409 es el respaldo para cuando el aviso se pierde.
  execFileSync('docker', ['exec', '-e', `SQLCMDPASSWORD=${process.env.MSSQL_SA}`, 'helpdesk-sqlserver-1', '/opt/mssql-tools18/bin/sqlcmd',
    '-C', '-I', '-b', '-S', 'localhost', '-U', 'sa', '-d', 'HelpDesk', '-Q', `UPDATE dbo.Tickets SET Status = N'Closed' WHERE Id = ${newId};`], { stdio: 'pipe' });
  const closed = await callApi('GET', `/api/tickets/${newId}`, lauraJwt);
  check('el ticket se cierra mientras camila escribe, sin aviso en vivo', closed.json.status === 'Closed', closed.json.status);
  await page.fill('#reply', 'Sigue sin imprimir.');
  await page.getByRole('button', { name: 'Responder' }).click();
  await page.getByText('Este ticket está cerrado').waitFor();
  const closedFacts = await page.locator('.ticket-facts').innerText();
  check('409: la pantalla recarga el ticket y lo muestra Cerrado, sin formulario',
    closedFacts.includes('Cerrado') && (await page.locator('#reply').count()) === 0, closedFacts.replace(/\s+/g, ' '));
  await shot('cerrado');

  console.log(`--- [${mode}] 8. Tickets ajenos y rutas inválidas ---`);
  await page.goto(`${portal}/tickets/${diegoTicket}`);
  await page.getByRole('heading', { name: 'No encontramos ese ticket' }).waitFor();
  check(`ticket de diego (${diegoTicket}): "No encontramos ese ticket"`, true);
  const beforeInvalid = requests.length;
  await page.goto(`${portal}/tickets/abc`);
  await page.getByRole('heading', { name: 'No encontramos esa página' }).waitFor();
  check('/tickets/abc: página no encontrada, sin llamar a /api/portal', !requests.slice(beforeInvalid).some((r) => r.includes('/api/portal')));

  console.log(`--- [${mode}] 9. Paginación con "Cargar más" ---`);
  for (let i = 1; i <= 18; i++) {
    const response = await page.request.post(`${portal}/api/portal/tickets`, {
      headers: { 'X-CSRF': '1' },
      data: { title: `E2E ${mode} paginación ${i}`, description: 'Ticket para probar Cargar más.', priority: 'Low' },
    });
    if (!response.ok()) throw new Error(`crear ticket ${i}: ${response.status()} ${await response.text()}`);
    created.push((await response.json()).id);
  }
  await page.goto(`${portal}/`);
  await rows.first().waitFor();
  const loadMore = page.getByRole('button', { name: 'Cargar más' });
  check('primera página: 20 filas y "Cargar más"', (await rows.count()) === 20 && (await loadMore.isVisible()), `filas=${await rows.count()}`);
  await loadMore.click();
  await page.waitForFunction(() => document.querySelectorAll('table.tickets tbody tr').length > 20);
  const total = initialRows + created.length;
  check(`"Cargar más": ${total} filas y el botón desaparece (nextAfter = null)`, (await rows.count()) === total && (await loadMore.count()) === 0, `filas=${await rows.count()}`);
  const pageRequests = requests.filter((r) => r.startsWith('GET /api/portal/tickets') && !/\d/.test(r));
  check('dos llamadas a la lista (After=0 y After=<nextAfter>)', pageRequests.length >= 2, pageRequests.length.toString());
  await shot('lista');

  await page.getByRole('link', { name: 'Cerrar sesión' }).click();
  await page.getByRole('heading', { name: 'Soporte para clientes' }).waitFor();
  check('cerrar sesión', true);

  check('sin errores en la consola (incluye violaciones de CSP)', consoleErrors.length === 0, consoleErrors.join(' | '));
} catch (error) {
  fail++;
  console.log(`FAIL  excepción: ${error.message.split('\n')[0]}`);
  await shot('error');
} finally {
  await browser.close();
  const deleted = [];
  for (const id of created) deleted.push((await callApi('DELETE', `/api/tickets/${id}`, lauraJwt)).status);
  console.log(`--- Limpieza: ${created.length} tickets borrados (status ${[...new Set(deleted)].join(',')}) ---`);
}

console.log(`RESULTADO [${mode}]: ${pass} PASS, ${fail} FAIL`);
