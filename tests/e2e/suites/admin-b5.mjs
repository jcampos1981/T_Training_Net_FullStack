// Prueba de punta a punta del panel Blazor (Fase 6, Bloque 5: formularios, conflictos, dashboard y reasignación).
// Uso: tests/e2e/run.ps1 -Suite admin-b5  (por dentro: node admin-b5.mjs <carpeta-deps> <carpeta-capturas> <raíz-del-repo>)
//   Contraseña en HELPDESK_DEV_PASSWORD. La API debe estar corriendo; el script levanta el panel.
import { execFileSync, execSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const admin = 'https://localhost:7300';
const api = 'http://localhost:5159';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

let adminProcess;
let adminLog = '';
async function startAdmin() {
  adminProcess = spawn(path.join(repo, 'src/HelpDesk.Admin/bin/Debug/net10.0/HelpDesk.Admin.exe'), [], {
    cwd: path.join(repo, 'src/HelpDesk.Admin'),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: admin },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  adminProcess.stdout.on('data', (chunk) => { adminLog += chunk; });
  adminProcess.stderr.on('data', (chunk) => { adminLog += chunk; });
  for (let i = 0; i < 120 && !adminLog.includes('Application started'); i++) await sleep(250);
  if (!adminLog.includes('Application started')) throw new Error(`El panel no arrancó:\n${adminLog}`);
}

let apiIsDown = false;
async function startApi() {
  spawn('dotnet', ['run', '--no-build', '--project', 'src/HelpDesk.Api', '--launch-profile', 'http'], { cwd: repo, detached: true, stdio: 'ignore', windowsHide: true }).unref();
  for (let i = 0; i < 120; i++) {
    const up = await fetch(`${api}/openapi/v1.json`).then((response) => response.ok).catch(() => false);
    if (up) { apiIsDown = false; return; }
    await sleep(500);
  }
  throw new Error('La API no volvió a arrancar.');
}

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

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1360, height: 900 } });
const page = await context.newPage();
const shot = (name) => page.screenshot({ path: path.join(shotsDir, `admin-b5-${name}.png`), fullPage: true });

const consoleErrors = [];
page.on('console', (message) => {
  if (message.type() !== 'error') return;
  const url = message.location().url ?? '';
  if (url && !url.startsWith(admin)) return;
  consoleErrors.push(`${message.text()} @ ${url}`);
});
page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));
let documentRequests = 0;
page.on('request', (request) => { if (request.isNavigationRequest() && request.url().startsWith(admin)) documentRequests++; });

async function login(user, target = '/') {
  await page.goto(`${admin}${target}`);
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL((url) => url.origin === admin && !url.pathname.startsWith('/signin-oidc'));
}
async function logout() {
  await page.getByRole('button', { name: 'Cerrar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  await page.locator('#username').waitFor();
}
// Como lo haría una persona: escribe y sale del campo (Blazor recibe el cambio al perder el foco).
async function typeInto(label, text) {
  const field = page.getByLabel(label, { exact: true });
  await field.fill(text);
  await field.press('Tab');
}
async function chooseInSelect(label, option) {
  await page.locator('.mud-select', { hasText: label }).first().click();
  await page.locator('.mud-popover-open .mud-list-item', { hasText: option }).first().click();
  await page.locator('.mud-popover-open .mud-list-item').first().waitFor({ state: 'detached' }).catch(() => {});
}
const button = (name) => page.getByRole('button', { name, exact: true });
const snackbar = (text) => page.locator('.mud-snackbar', { hasText: text });
const fieldError = (text) => page.locator('.mud-input-helper-text.mud-input-error', { hasText: text }).or(page.locator('.mud-input-error', { hasText: text })).first();

const created = [];
let lauraJwt;

try {
  await startAdmin();
  lauraJwt = await jwt('laura@helpdesk.local');
  const pedroJwt = await jwt('pedro@helpdesk.local');
  const newTicket = async (title, assignedTo) => {
    const ticket = (await callApi('POST', '/api/tickets', lauraJwt, { title, priority: 'Medium', assignedTo })).json;
    created.push(ticket.id);
    return ticket;
  };
  // "Otra persona" guarda: laura, por la API, mientras pedro tiene el formulario abierto en el navegador.
  const lauraSaves = async (id, changes) => {
    const current = (await callApi('GET', `/api/tickets/${id}`, lauraJwt)).json;
    return callApi('PUT', `/api/tickets/${id}`, lauraJwt, {
      title: current.title, priority: current.priority, status: current.status, assignedTo: current.assignedTo, version: current.version, ...changes,
    });
  };
  const ticketNow = async (id) => (await callApi('GET', `/api/tickets/${id}`, lauraJwt)).json;

  const editTicket = await newTicket('E2E edición: la VPN se cae', 'pedro');
  const closedWhileEditing = await newTicket('E2E: lo cierran mientras se edita', 'pedro');
  const closedWhileCommenting = await newTicket('E2E: lo cierran mientras se comenta', 'pedro');
  const toReassign = [await newTicket('E2E reasignar 1', 'e2e-origen'), await newTicket('E2E reasignar 2', 'e2e-origen'), await newTicket('E2E reasignar 3', 'e2e-origen')];

  console.log('--- 1. pedro (Agent): lo que no le corresponde ---');
  await login('pedro', `/tickets/${editTicket.id}`);
  await button('Editar').waitFor();
  check('el menú de un agente no tiene Dashboard ni Agentes', (await page.getByRole('link', { name: 'Dashboard' }).count()) === 0 && (await page.getByRole('link', { name: 'Agentes' }).count()) === 0);
  await page.evaluate(() => window.Blazor.navigateTo('/dashboard'));
  await page.getByText('No tienes permiso para ver esta página.').waitFor();
  check('navegando dentro del circuito: [Authorize] lo detiene (AuthorizeRouteView)', true, new URL(page.url()).pathname);
  await page.goto(`${admin}/agentes`);
  await page.getByRole('heading', { name: 'No tienes permiso para ver esa página' }).waitFor();
  check('abriendo la URL: página estática, con el mensaje correcto para un agente', page.url().includes('/acceso-denegado'), new URL(page.url()).pathname);
  await page.getByRole('link', { name: 'Volver a los tickets' }).click();
  await page.locator('.mud-table-body tr').first().waitFor();
  check('y puede volver: su sesión sigue activa', true);

  console.log('--- 2. Formulario: validación con EditForm ---');
  await page.goto(`${admin}/tickets/${editTicket.id}`);
  await button('Editar').click();
  check('un agente no ve el campo Agente: reasignar no le corresponde', (await page.getByLabel('Agente', { exact: true }).count()) === 0);
  const versionBefore = (await ticketNow(editTicket.id)).version;
  await typeInto('Título', '');
  await button('Guardar').click();
  await fieldError('El título es obligatorio.').waitFor();
  check('título vacío: mensaje de [Required] junto al campo', true);
  await typeInto('Título', 'abc');
  await button('Guardar').click();
  await fieldError('El título debe tener entre 5 y 120 caracteres.').waitFor();
  check('título corto: mensaje de [StringLength]', true);
  check('esos errores no llegaron a la API: el ticket no cambió', (await ticketNow(editTicket.id)).version === versionBefore);
  await shot('form-errores');

  const documentsBeforeSave = documentRequests;
  await typeInto('Título', 'E2E edición: la VPN se cae cada hora');
  await chooseInSelect('Prioridad', 'Alta');
  await chooseInSelect('Estado', 'En atención');
  await button('Guardar').click();
  await snackbar('Cambios guardados.').waitFor();
  await page.getByRole('heading', { name: 'la VPN se cae cada hora' }).waitFor();
  let saved = await ticketNow(editTicket.id);
  check('datos válidos: se guardan los tres campos', saved.title === 'E2E edición: la VPN se cae cada hora' && saved.priority === 'High' && saved.status === 'InProgress', `${saved.priority} ${saved.status}`);
  check('el agente no cambió (PUT reemplaza todo: el formulario lo conserva)', saved.assignedTo === 'pedro', saved.assignedTo);
  const facts = (await page.locator('.ticket-facts').innerText()).replace(/\s+/g, ' ');
  check('la pantalla muestra lo nuevo, sin recargar', facts.includes('En atención') && facts.includes('Alta') && documentRequests === documentsBeforeSave, facts.slice(0, 50));

  console.log('--- 3. Dos personas editan a la vez (concurrencia optimista) ---');
  await button('Editar').click();
  await typeInto('Título', 'E2E: el título que quiere pedro');
  check('laura guarda primero, por la API', (await lauraSaves(editTicket.id, { title: 'E2E: el título que quiere laura' })).status === 200);
  await button('Guardar').click();
  const conflict = page.getByTestId('edit-conflict');
  await conflict.waitFor();
  const conflictText = (await conflict.innerText()).replace(/\s+/g, ' ');
  check('pedro guarda con una versión vieja: 409 y se le explica', conflictText.includes('Conflicto de edición'), conflictText.slice(0, 60));
  check('se le muestra lo que hay ahora', conflictText.includes('el título que quiere laura'));
  check('lo que escribió no se pierde', (await page.getByLabel('Título', { exact: true }).inputValue()) === 'E2E: el título que quiere pedro');
  check('la base no aplicó el cambio de pedro', (await ticketNow(editTicket.id)).title === 'E2E: el título que quiere laura');
  await shot('conflicto');
  await button('Conservar lo mío').click();
  await button('Guardar').click();
  await snackbar('Cambios guardados.').first().waitFor();
  await page.getByRole('heading', { name: 'el título que quiere pedro' }).waitFor();
  check('"Conservar lo mío": guarda con la versión nueva', (await ticketNow(editTicket.id)).title === 'E2E: el título que quiere pedro');

  await button('Editar').click();
  await typeInto('Título', 'E2E: esto se va a descartar');
  await lauraSaves(editTicket.id, { title: 'E2E: laura otra vez', priority: 'Critical' });
  await button('Guardar').click();
  await conflict.waitFor();
  await button('Cargar lo actual (descarta lo mío)').click();
  await conflict.waitFor({ state: 'detached' });
  check('"Cargar lo actual": el formulario pasa a los datos de laura', (await page.getByLabel('Título', { exact: true }).inputValue()) === 'E2E: laura otra vez');
  check('y el resto de la pantalla también', (await page.locator('h1').innerText()).includes('laura otra vez'));
  await button('Cancelar').click();
  await button('Editar').waitFor();
  check('cancelar no toca nada', (await ticketNow(editTicket.id)).title === 'E2E: laura otra vez');

  await page.goto(`${admin}/tickets/${closedWhileEditing.id}`);
  await button('Editar').click();
  await typeInto('Título', 'E2E: un cambio que llega tarde');
  await lauraSaves(closedWhileEditing.id, { status: 'Closed' });
  await button('Guardar').click();
  await conflict.waitFor();
  const closedConflict = (await conflict.innerText()).replace(/\s+/g, ' ');
  check('ticket cerrado mientras se editaba: 409 con la regla de negocio', closedConflict.includes('cerrado'), closedConflict.slice(0, 70));
  check('ya no se ofrece "Conservar lo mío": no serviría', (await button('Conservar lo mío').count()) === 0);
  await button('Cargar lo actual (descarta lo mío)').click();
  await page.getByTestId('ticket-closed').waitFor();
  check('al cargar lo actual se ve cerrado, sin formularios', (await button('Editar').count()) === 0 && (await button('Comentar').count()) === 0);

  console.log('--- 4. Comentarios ---');
  await page.goto(`${admin}/tickets/${editTicket.id}`);
  await button('Comentar').waitFor();
  await button('Comentar').click();
  await fieldError('Escribe el comentario.').waitFor();
  check('comentario vacío: mensaje junto al campo', true);
  const documentsBeforeComment = documentRequests;
  await typeInto('Agregar un comentario', 'Revisé el concentrador.\nSigo investigando.');
  await button('Comentar').click();
  await page.locator('.comment').first().waitFor();
  check('el comentario aparece en la conversación, firmado', (await page.locator('.comment-body').last().innerText()) === 'Revisé el concentrador.\nSigo investigando.' && (await page.locator('.comment-meta').last().innerText()).includes('pedro'));
  check('el campo queda vacío y sin errores', (await page.getByLabel('Agregar un comentario').inputValue()) === '');
  check('sin recargar la página', documentRequests === documentsBeforeComment);
  await shot('detalle-editable');

  await page.goto(`${admin}/tickets/${closedWhileCommenting.id}`);
  await button('Comentar').waitFor();
  await typeInto('Agregar un comentario', 'Esto llega tarde.');
  // Directo a la base: sin aviso en vivo. Con aviso, el formulario desaparece antes del clic (lo cubre admin-live-e2e);
  // el 409 es el respaldo para cuando el aviso se pierde.
  execFileSync('docker', ['exec', '-e', `SQLCMDPASSWORD=${process.env.MSSQL_SA}`, 'helpdesk-sqlserver-1', '/opt/mssql-tools18/bin/sqlcmd',
    '-C', '-I', '-b', '-S', 'localhost', '-U', 'sa', '-d', 'HelpDesk', '-Q', `UPDATE dbo.Tickets SET Status = N'Closed' WHERE Id = ${closedWhileCommenting.id};`], { stdio: 'pipe' });
  await button('Comentar').click();
  await page.getByTestId('ticket-closed').waitFor();
  check('cerrado mientras se comentaba: 409, aviso y la pantalla se actualiza', (await snackbar('no admite comentarios').count()) === 1 && (await button('Comentar').count()) === 0);

  await page.goto(`${admin}/tickets/${editTicket.id}`);
  await button('Editar').click();
  await chooseInSelect('Estado', 'Cerrado');
  await button('Guardar').click();
  await page.getByTestId('ticket-closed').waitFor();
  check('pedro cierra su ticket: se bloquea edición y comentarios', (await ticketNow(editTicket.id)).status === 'Closed' && (await button('Editar').count()) === 0);
  await logout();

  console.log('--- 5. laura (Supervisor): dashboard ---');
  await login('laura', '/dashboard');
  await page.getByRole('heading', { name: 'Carga abierta por agente' }).waitFor();
  check('supervisión ve Dashboard y Agentes en el menú', (await page.getByRole('link', { name: 'Dashboard' }).count()) === 1 && (await page.getByRole('link', { name: 'Agentes' }).count()) === 1);
  const dashboard = (await callApi('GET', '/api/dashboard', lauraJwt)).json;
  for (const status of ['Open', 'InProgress', 'Resolved', 'Closed']) {
    const expected = dashboard.ticketsByStatus.find((item) => item.status === status)?.total ?? 0;
    const shown = Number((await page.getByTestId(`status-${status}`).locator('.stat-value').innerText()).replace(/\D/g, ''));
    check(`indicador ${status} igual a la API`, shown === expected, `${shown}`);
  }
  const workloadRows = page.getByTestId('workload-row');
  check('una fila por agente con tickets abiertos', (await workloadRows.count()) === dashboard.openWorkload.length, `${await workloadRows.count()} filas`);
  const barWidths = await page.$$eval('.bar', (bars) => bars.map((bar) => bar.getBoundingClientRect().width));
  check('barras de mayor a menor', barWidths.every((width, index) => index === 0 || width <= barWidths[index - 1] + 0.5), barWidths.slice(0, 4).map((w) => Math.round(w)).join(','));
  const largest = Math.max(...dashboard.openWorkload.map((row) => row.openTickets));
  const smallest = Math.min(...dashboard.openWorkload.map((row) => row.openTickets));
  check('el ancho de cada barra es proporcional', Math.abs(barWidths.at(-1) / barWidths[0] - Math.max(smallest / largest, 2 / barWidths[0])) < 0.02, `${smallest}/${largest} → ${(barWidths.at(-1) / barWidths[0]).toFixed(3)}`);

  const dayCells = page.getByTestId('comments-table').locator('tbody td');
  check('la tabla de comentarios tiene los 7 días, aunque alguno no tenga datos', (await dayCells.count()) === 7, `${await dayCells.count()} días`);
  const byDay = new Map(dashboard.commentsLast7Days.map((row) => [row.day.slice(0, 10), row.comments]));
  const today = new Date(dashboard.generatedAt);
  const expectedDays = Array.from({ length: 7 }, (_, index) => {
    const day = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - 6 + index));
    return byDay.get(day.toISOString().slice(0, 10)) ?? 0;
  });
  const shownDays = (await dayCells.allInnerTexts()).map((text) => Number(text.replace(/\D/g, '')));
  // Hoy puede haber sumado comentarios (la limpieza no los quita hasta el final): se comparan los días cerrados.
  check('cada día coincide con la API, y los días sin comentarios valen 0', expectedDays.slice(0, 6).every((value, index) => value === shownDays[index]) && shownDays[6] >= expectedDays[6] - 1, shownDays.join(','));
  check('el gráfico es una línea SVG', (await page.locator('.mud-chart svg path').count()) > 0);
  await shot('dashboard');
  const updatedBefore = await page.getByTestId('dashboard-updated').innerText();
  await sleep(1100);
  await button('Actualizar').click();
  await button('Actualizar').waitFor();
  check('"Actualizar" mantiene los datos en pantalla mientras carga', (await workloadRows.count()) > 0, `${updatedBefore.trim()}`);

  console.log('--- 6. Reasignación con diálogo ---');
  await page.getByRole('link', { name: 'Agentes' }).click();
  const originRow = page.locator('.mud-table-body tr', { hasText: 'e2e-origen' });
  await originRow.waitFor();
  check('la tabla lista a e2e-origen con 3 abiertos', (await originRow.locator('td[data-label="Abiertos"]').innerText()) === '3');
  await originRow.getByRole('button', { name: 'Reasignar' }).click();
  await page.getByRole('heading', { name: 'Reasignar los tickets de e2e-origen' }).or(page.getByText('Reasignar los tickets de e2e-origen')).first().waitFor();
  const dialog = page.locator('.mud-dialog');
  check('el diálogo explica qué va a pasar', (await dialog.innerText()).includes('3 tickets abiertos'));
  await dialog.getByRole('button', { name: 'Reasignar', exact: true }).click();
  await fieldError('Indica el agente destino.').waitFor();
  check('sin destino: validación del formulario', true);
  await typeInto('Agente destino', 'e2e-origen');
  await dialog.getByRole('button', { name: 'Reasignar', exact: true }).click();
  await fieldError('El agente destino debe ser distinto del de origen.').waitFor();
  check('destino igual al origen: regla que solo conoce la API, mostrada junto al campo', true);
  check('el diálogo sigue abierto para corregir', await dialog.isVisible());
  await shot('dialogo');
  await dialog.getByRole('button', { name: 'Cancelar' }).click();
  await dialog.waitFor({ state: 'detached' });
  check('cancelar no mueve nada', (await ticketNow(toReassign[0].id)).assignedTo === 'e2e-origen');

  await originRow.getByRole('button', { name: 'Reasignar' }).click();
  await dialog.waitFor();
  await typeInto('Agente destino', 'e2e-destino');
  await dialog.getByRole('button', { name: 'Reasignar', exact: true }).click();
  await snackbar('Se movieron 3 tickets de e2e-origen a e2e-destino.').waitFor();
  check('reasignar: el diálogo se cierra y confirma', !(await dialog.isVisible()));
  await page.locator('.mud-table-body tr', { hasText: 'e2e-destino' }).waitFor();
  check('la tabla se actualiza: e2e-origen ya no tiene abiertos', (await originRow.count()) === 0);
  const moved = await Promise.all(toReassign.map((ticket) => ticketNow(ticket.id)));
  check('la API confirma los tres en e2e-destino', moved.every((ticket) => ticket.assignedTo === 'e2e-destino'));
  const trail = (await callApi('GET', `/api/tickets/${toReassign[0].id}/comments`, lauraJwt)).json;
  check('y queda el comentario en cada ticket', trail.some((comment) => comment.author === 'sistema' && comment.body.includes('e2e-destino')), trail[0]?.body);

  console.log('--- 7. Supervisión reasigna desde el detalle ---');
  await page.goto(`${admin}/tickets/${toReassign[0].id}`);
  await button('Editar').click();
  await typeInto('Agente', 'luis');
  await button('Guardar').click();
  await snackbar('Cambios guardados.').waitFor();
  check('cambia el agente de un ticket', (await ticketNow(toReassign[0].id)).assignedTo === 'luis');
  await button('Editar').click();
  await typeInto('Agente', '');
  await button('Guardar').click();
  await page.locator('.ticket-facts', { hasText: 'Sin asignar' }).waitFor();
  saved = await ticketNow(toReassign[0].id);
  check('campo vacío: queda sin asignar', saved.assignedTo === null, String(saved.assignedTo));

  console.log('--- 8. La API se cae con un diálogo abierto ---');
  await page.goto(`${admin}/agentes`);
  const destinationRow = page.locator('.mud-table-body tr', { hasText: 'e2e-destino' });
  await destinationRow.waitFor();
  await destinationRow.getByRole('button', { name: 'Reasignar' }).click();
  await dialog.waitFor();
  await typeInto('Agente destino', 'e2e-otro');
  execSync('taskkill /F /IM HelpDesk.Api.exe', { stdio: 'ignore' });
  apiIsDown = true;
  await sleep(500);
  await dialog.getByRole('button', { name: 'Reasignar', exact: true }).click();
  await page.getByTestId('api-error').waitFor({ timeout: 30000 });
  check('el error dentro del diálogo llega a la barrera', (await page.getByTestId('api-error').innerText()).includes('No se pudo conectar'), (await page.getByTestId('api-error').innerText()).replace(/\s+/g, ' '));
  check('el circuito sigue vivo', !(await page.locator('#blazor-error-ui').isVisible()) && (await page.getByTestId('current-user').innerText()) === 'laura');
  await shot('api-caida');
  await startApi();
  await button('Reintentar').click();
  await destinationRow.waitFor();
  check('"Reintentar": la pantalla vuelve, sin perder la sesión', (await page.getByTestId('current-user').innerText()) === 'laura');
  check('el reintento no movió nada: la operación nunca llegó', (await ticketNow(toReassign[1].id)).assignedTo === 'e2e-destino');
  await page.locator('.mud-table-body tr', { hasText: 'e2e-destino' }).getByRole('button', { name: 'Reasignar' }).click();
  await dialog.waitFor();
  check('y los diálogos siguen funcionando después del error', await dialog.isVisible());
  await dialog.getByRole('button', { name: 'Cancelar' }).click();

  check('sin errores en la consola (incluye violaciones de CSP)', consoleErrors.length === 0, consoleErrors.join(' | '));
  check('ningún error terminó el circuito', !/Unhandled exception/i.test(adminLog));
} catch (error) {
  fail++;
  console.log(`FAIL  excepción: ${error.message.split('\n')[0]}`);
  await shot('error').catch(() => {});
  console.log(`      url=${page.url()}`);
  console.log(adminLog.split('\n').filter((line) => /fail:|Exception/i.test(line)).slice(-8).join('\n'));
} finally {
  await browser.close();
  adminProcess?.kill();
  if (apiIsDown) await startApi();
  if (lauraJwt) {
    const deleted = [];
    for (const id of created) deleted.push((await callApi('DELETE', `/api/tickets/${id}`, lauraJwt)).status);
    console.log(`--- Limpieza: ${created.length} tickets borrados (status ${[...new Set(deleted)].join(',')}) ---`);
  }
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
