// La aplicación en contenedores, con las imágenes de producción, en Edge headless.
// Uso: tests/e2e/run.ps1 -Suite containers -NoBuild  (por dentro: node containers.mjs <deps> <capturas> <repo>    Contraseña en HELPDESK_DEV_PASSWORD.)
//   run.ps1 levanta deploy/compose.yaml + deploy/compose.app.yaml. Esta suite recrea contenedores de verdad.
import { execFileSync, spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const portal = 'http://localhost:8081';
const panel = 'http://localhost:8082';
const tag = `contenedores-${Date.now()}`;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
function section(title) { console.log(`--- ${title} ---`); }

const docker = (...args) => execFileSync('docker', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const attempt = (...args) => spawnSync('docker', args, { cwd: repo, encoding: 'utf8' });
const compose = (...args) => docker('compose', '-f', 'deploy/compose.yaml', '-f', 'deploy/compose.app.yaml', ...args);
const inspect = (container, format) => docker('inspect', '--format', format, `helpdesk-${container}-1`);
const recreate = (service) => compose('up', '-d', '--no-deps', '--force-recreate', '--wait', '--wait-timeout', '120', service);

const has = (locator, timeout = 20_000) => locator.first().waitFor({ timeout }).then(() => true, () => false);

async function waitLogs(condition, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (condition()) return true;
    await sleep(1000);
  }
  return condition();
}

async function signIn(page, user) {
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
}

async function signInToPortal(page, user) {
  await page.goto(portal);
  await page.getByRole('link', { name: 'Iniciar sesión' }).click();
  await signIn(page, user);
  await page.waitForURL((url) => url.origin === portal && !url.pathname.startsWith('/signin-oidc'));
}

// Una Content-Security-Policy rota o un archivo que no llegó no se ven en desarrollo: solo en la aplicación publicada.
function watchForBrokenPage(page, problems) {
  page.on('console', (message) => {
    const source = message.location().url ?? '';
    const ours = source.startsWith(portal) || source.startsWith(panel);
    if (ours && message.type() === 'error' && /Content Security Policy|Refused to|Failed to load resource.*(404|500)/.test(message.text())) {
      problems.push(`${message.text()} @ ${source}`);
    }
  });
  page.on('pageerror', (error) => problems.push(String(error)));
}

section('Las imágenes');
for (const service of ['api', 'bff', 'admin', 'worker']) {
  const user = inspect(service, '{{.Config.User}}');
  check(`${service}: no corre como root`, user !== '' && user !== 'root' && user !== '0', `usuario ${user}`);
  check(`${service}: sano según Docker`, inspect(service, '{{.State.Health.Status}}') === 'healthy');
  check(`${service}: el disco de la imagen es de solo lectura`, inspect(service, '{{.HostConfig.ReadonlyRootfs}}') === 'true');
  check(`${service}: sin capacidades de Linux`, inspect(service, '{{.HostConfig.CapDrop}}').includes('ALL'));
  check(`${service}: no trae shell`, attempt('exec', `helpdesk-${service}-1`, 'sh', '-c', 'echo hola').status !== 0);
  console.log(`      ${service}: ${(Number(docker('image', 'inspect', '--format', '{{.Size}}', `helpdesk-${service}:local`)) / 1024 / 1024).toFixed(0)} MB`);
}
check('Las migraciones terminaron bien', inspect('migrator', '{{.State.ExitCode}}') === '0');
check('La API no se publica en la máquina', inspect('api', '{{json .NetworkSettings.Ports}}').includes('null') || !inspect('api', '{{json .NetworkSettings.Ports}}').includes('HostPort'));
check('La configuración de desarrollo no está en la imagen', attempt('cp', 'helpdesk-api-1:/app/appsettings.Development.json', '-').status !== 0);
check('El portal compilado sí está en la imagen del BFF', attempt('cp', 'helpdesk-bff-1:/app/wwwroot/index.html', '-').status === 0);
const secretsInHistory = ['MSSQL_SA', 'HELPDESK_DEV_PASSWORD']
  .map((name) => process.env[name])
  .filter(Boolean)
  .filter((secret) => ['api', 'bff', 'admin'].some((service) => docker('history', '--no-trunc', `helpdesk-${service}:local`).includes(secret)));
check('Ningún secreto en las capas de las imágenes', secretsInHistory.length === 0);

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const created = [];
const problems = [];

try {
  const customerContext = await browser.newContext({ locale: 'es-CL', viewport: { width: 1200, height: 800 } });
  const customer = await customerContext.newPage();
  watchForBrokenPage(customer, problems);
  const comments = customer.locator('.comment-body');

  section('Portal: login contra Keycloak y un ticket nuevo');
  await signInToPortal(customer, 'camila');
  check('Inicia sesión: Keycloak en una dirección para el navegador y otra para el servidor', new URL(customer.url()).origin === portal);
  const cookies = await customerContext.cookies(portal);
  const session = cookies.find((cookie) => cookie.name === '__Host-helpdesk');
  check('La cookie de sesión es HttpOnly y Secure', session?.httpOnly === true && session?.secure === true);

  await customer.goto(`${portal}/tickets/new`);
  check('Una ruta de React sirve index.html', await has(customer.locator('#title')));
  await customer.fill('#title', `${tag} impresora`);
  await customer.fill('#description', 'Probando la aplicación en contenedores.');
  await customer.getByRole('button', { name: 'Abrir ticket' }).click();
  await customer.waitForURL(/\/tickets\/\d+/);
  const ticketId = Number(new URL(customer.url()).pathname.split('/').pop());
  created.push(ticketId);
  check('Crea el ticket: BFF → API → SQL Server', ticketId > 0, `#${ticketId}`);

  const assets = await customer.evaluate(() => [...document.scripts].map((script) => script.src).filter(Boolean));
  const asset = await fetch(assets[0]);
  check('Los archivos con hash se guardan un año en caché', (asset.headers.get('cache-control') ?? '').includes('immutable'));
  const index = await fetch(`${portal}/`);
  check('index.html se revalida siempre', index.headers.get('cache-control') === 'no-cache');
  check('Lleva Content-Security-Policy', (index.headers.get('content-security-policy') ?? '').includes("default-src 'self'"));

  section('Panel: login, y un comentario que llega en vivo al portal');
  // Docker dice que está sano aunque falte: el health check no carga la página. Sin este script, el panel queda en blanco.
  check('La imagen trae el script de Blazor', (await fetch(`${panel}/_framework/blazor.web.js`)).status === 200);
  const staffContext = await browser.newContext({ locale: 'es-CL', viewport: { width: 1400, height: 900 } });
  const staff = await staffContext.newPage();
  watchForBrokenPage(staff, problems);
  await staff.goto(`${panel}/tickets/${ticketId}`);
  await signIn(staff, 'laura');
  await staff.waitForURL((url) => url.origin === panel && !url.pathname.startsWith('/signin-oidc'));
  const commentField = staff.getByLabel('Agregar un comentario', { exact: true });
  check('El panel abre el ticket del cliente', await has(commentField));

  const comment = async (text) => {
    await commentField.fill(text);
    await commentField.press('Tab');
    await staff.getByRole('button', { name: 'Comentar', exact: true }).click();
  };

  await comment('Estamos revisando tu impresora.');
  check('El comentario aparece en el portal sin recargar: panel → API → Redis → hub → BFF → navegador', await has(comments.filter({ hasText: 'Estamos revisando tu impresora.' })));
  await staff.screenshot({ path: path.join(shotsDir, 'f9-panel.png') });
  await customer.screenshot({ path: path.join(shotsDir, 'f9-portal.png') });

  // Sin ICU, .NET no puede formatear en es-CL: saldría "Nov", en inglés y con mayúscula.
  check('Las fechas salen en español: la imagen trae ICU', await has(staff.locator('time', { hasText: /\d{1,2} (ene|feb|mar|abr|may|jun|jul|ago|sept?|oct|nov|dic)\.? \d{4}/ })));
  // Antes de recrear nada: después, el navegador registra los errores de red que la prueba provoca.
  check('Nada roto en la consola: scripts, estilos ni Content-Security-Policy', problems.length === 0, problems.slice(0, 3).join(' | '));

  section('Eventos: el worker avisa al cliente');
  const code = `T-${String(ticketId).padStart(5, '0')}`;
  const workerLogs = () => docker('logs', 'helpdesk-worker-1');
  const emailSent = (text) => waitLogs(() => workerLogs().includes(text));
  check('El worker recibió el evento del ticket nuevo: pasó por el outbox y RabbitMQ', await emailSent(`Correo enviado sobre ${code}: Recibimos`));

  section('RabbitMQ caído: no se pierde ningún evento');
  compose('stop', 'rabbitmq');
  await customer.goto(`${portal}/tickets/new`);
  await customer.fill('#title', `${tag} sin rabbit`);
  await customer.fill('#description', 'Creado con RabbitMQ detenido.');
  await customer.getByRole('button', { name: 'Abrir ticket' }).click();
  await customer.waitForURL(new RegExp(`/tickets/(?!${ticketId}$)\\d+`));
  const offlineId = Number(new URL(customer.url()).pathname.split('/').pop());
  created.push(offlineId);
  const offlineCode = `T-${String(offlineId).padStart(5, '0')}`;
  check('El cliente puede crear su ticket igual', offlineId > 0, offlineCode);
  await sleep(8000);
  check('Y el aviso todavía no salió', !workerLogs().includes(`Correo enviado sobre ${offlineCode}`));
  const degraded = await fetch(`${portal}/health/ready`);
  check('La salud del BFF no se afecta', degraded.status === 200, `${degraded.status}`);
  compose('start', 'rabbitmq');
  check('RabbitMQ vuelve', await waitLogs(() => inspect('rabbitmq', '{{.State.Health.Status}}') === 'healthy', 90_000));
  check('El evento pendiente sale solo, y el worker lo recibe', await emailSent(`Correo enviado sobre ${offlineCode}: Recibimos`));
  await customer.goto(`${portal}/tickets/${ticketId}`);
  await comments.first().waitFor({ timeout: 20_000 }).catch(() => {});

  section('Las trazas llegan al dashboard');
  const traces = await fetch('http://localhost:18888/api/telemetry/resources').then((response) => response.json()).catch(() => []);
  const reporting = traces.map?.((resource) => resource.name) ?? [];
  check('El BFF, la API, el panel y el worker envían telemetría', ['helpdesk-api', 'helpdesk-bff', 'helpdesk-admin', 'helpdesk-worker'].every((name) => reporting.includes(name)), reporting.join(', '));

  section('Recrear un contenedor: la imagen se pierde, el volumen no');
  recreate('bff');
  check('BFF recreado: sigue sano', inspect('bff', '{{.State.Health.Status}}') === 'healthy');
  await staff.goto(`${panel}/tickets/${ticketId}`);
  await has(commentField);
  await comment('Comentario después de recrear el BFF.');
  check('El portal vuelve a conectarse solo, y lo recibe sin recargar', await has(comments.filter({ hasText: 'Comentario después de recrear el BFF.' }), 60_000));
  await customer.reload();
  check('La sesión del cliente sobrevive: las llaves están en el volumen', await has(comments.filter({ hasText: 'Estamos revisando tu impresora.' })));
  check('No le pide iniciar sesión otra vez', await customer.getByRole('link', { name: 'Iniciar sesión' }).count() === 0);

  // Es lo que pasa en cada despliegue con un agente trabajando. El circuito y los tokens en memoria se pierden.
  await staff.goto(`${panel}/tickets/${ticketId}`);
  await has(commentField);
  const reloaded = staff.waitForEvent('load', { timeout: 120_000 }).then(() => true, () => false);
  recreate('admin');
  check('Panel recreado: la pestaña abierta se recupera sola', await reloaded);
  check('La sesión de Keycloak sigue viva: entra sin pedir contraseña', await has(commentField, 30_000) && await staff.locator('#username').count() === 0);
  await comment('Comentario después de recrear el panel.');
  check('Y el panel vuelve a funcionar', await has(comments.filter({ hasText: 'Comentario después de recrear el panel.' })));

  recreate('api');
  await customer.reload();
  check('API recreada: el portal sigue funcionando', await has(comments.filter({ hasText: 'Estamos revisando tu impresora.' }), 30_000));

  section('Salud');
  const bffHealth = await fetch(`${portal}/health/ready`);
  check('/health/ready del BFF responde', bffHealth.status === 200, await bffHealth.text());
  const panelHealth = await fetch(`${panel}/health/ready`);
  check('/health/ready del panel responde', panelHealth.status === 200, await panelHealth.text());

  section('Cerrar sesión');
  await customer.goto(portal);
  await customer.getByRole('link', { name: 'Cerrar sesión' }).click();
  check('El portal cierra la sesión, también en Keycloak', await has(customer.getByRole('link', { name: 'Iniciar sesión' }), 30_000));
} finally {
  // Con una sesión de supervisora en el BFF: la API no se publica fuera de la red de Compose.
  const cleanerContext = await browser.newContext();
  const cleaner = await cleanerContext.newPage();
  await signInToPortal(cleaner, 'laura').catch((error) => console.log(`No se pudo iniciar la limpieza: ${error.message}`));
  for (const id of created) {
    const status = await cleaner.evaluate(async (ticket) => (await fetch(`/api/tickets/${ticket}`, { method: 'DELETE', headers: { 'X-CSRF': '1' } })).status, id).catch(() => 0);
    if (status !== 204) console.log(`No se pudo borrar el ticket ${id}: ${status}`);
  }
  await browser.close();
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
process.exit(fail === 0 ? 0 : 1);
