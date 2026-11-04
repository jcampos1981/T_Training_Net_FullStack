// Con Redis caído, el hub acepta la conexión y la cierra enseguida: el portal y el panel no deben entrar en un bucle.
// Uso: tests/e2e/run.ps1 -Suite redis-clients  (por dentro: node redis-clients.mjs <deps> <capturas> <repo>    Contraseña en HELPDESK_DEV_PASSWORD.)
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const api = 'http://localhost:5159';
const portal = 'https://localhost:7200';
const admin = 'https://localhost:7300';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
function start(project, url) {
  const state = { log: '' };
  state.child = spawn(path.join(repo, `src/${project}/bin/Debug/net10.0/${project}.exe`), [], {
    cwd: path.join(repo, `src/${project}`),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: url },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  state.child.stdout.on('data', (chunk) => { state.log += chunk; });
  state.child.stderr.on('data', (chunk) => { state.log += chunk; });
  return state;
}
const started = async (state) => {
  for (let i = 0; i < 240 && !state.log.includes('Application started'); i++) await sleep(250);
  if (!state.log.includes('Application started')) throw new Error(state.log.slice(-1500));
};
const docker = (...args) => execFileSync('docker', args, { stdio: 'pipe' }).toString().trim();
const has = async (locator, ms = 15_000) => locator.first().waitFor({ timeout: ms }).then(() => true).catch(() => false);
const gone = async (locator, ms = 45_000) => locator.first().waitFor({ state: 'detached', timeout: ms }).then(() => true).catch(() => false);

async function jwt(email) {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${api}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
    if (response.ok) return (await response.json()).accessToken;
    if (attempt > 10) throw new Error(`token ${email}: ${response.status}`);
    await sleep(5000);
  }
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
async function login(page, base, user) {
  await page.goto(base);
  if (base === portal) await page.getByRole('link', { name: 'Iniciar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL((url) => url.origin === base && !url.pathname.startsWith('/signin-oidc'));
}

let apiState = start('HelpDesk.Api', api);
const bff = start('HelpDesk.Bff', portal);
const panel = start('HelpDesk.Admin', admin);
const created = [];
let redisStopped = false;
let lauraJwt;
const browser = await chromium.launch({ channel: 'msedge', headless: true });

try {
  await Promise.all([started(apiState), started(bff), started(panel)]);
  lauraJwt = await jwt('laura@helpdesk.local');
  const camilaJwt = await jwt('camila@acme.local');
  const ticket = (await callApi('POST', '/api/portal/tickets', camilaJwt, { title: 'redis clientes prueba', priority: 'Low', description: 'Descripción de prueba.' })).json;
  created.push(ticket.id);
  const pedroTicket = (await callApi('POST', '/api/tickets', lauraJwt, { title: 'redis clientes panel', priority: 'Low', assignedTo: 'pedro' })).json;
  created.push(pedroTicket.id);

  const portalContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
  const portalPage = await portalContext.newPage();
  await login(portalPage, portal, 'camila');
  await portalPage.goto(`${portal}/tickets/${ticket.id}`);
  await portalPage.locator('.comment-body').first().waitFor();

  const panelContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1360, height: 900 } });
  const panelPage = await panelContext.newPage();
  await login(panelPage, admin, 'pedro');
  await panelPage.goto(`${admin}/tickets/${pedroTicket.id}`);
  await panelPage.getByRole('heading', { name: /redis clientes panel/ }).waitFor();
  await sleep(1500);

  console.log('--- 1. Se cae Redis y se reinicia la API: todos deben reconectarse ---');
  docker('stop', 'helpdesk-redis-1');
  redisStopped = true;
  apiState.child.kill();
  await sleep(1500);
  apiState = start('HelpDesk.Api', api);
  await started(apiState);
  const windowSeconds = 25;
  await sleep(windowSeconds * 1000);

  const attempts = (apiState.log.match(/GET \/hubs\/tickets[^ ]* → /g) ?? []).length;
  const refused = (apiState.log.match(/Rate limit:/g) ?? []).length;
  check('Sin bucle: pocos intentos, con espera creciente (2 clientes en 25 s)', attempts > 0 && attempts <= 16, `${attempts} intentos`);
  check('El límite de peticiones no tuvo que frenarlos', refused === 0, `${refused} rechazos`);
  check('El portal avisa que no hay conexión en vivo', await has(portalPage.locator('.live-offline')));
  check('El panel avisa que no hay conexión en vivo', await has(panelPage.getByTestId('live-offline')));
  await portalPage.screenshot({ path: path.join(shotsDir, 'f7-redis-portal.png') });

  console.log('--- 2. La aplicación sigue funcionando sin Redis ---');
  const commentField = panelPage.getByLabel('Agregar un comentario', { exact: true });
  await commentField.fill('Comentario con Redis caído');
  await commentField.press('Tab');
  await panelPage.getByRole('button', { name: 'Comentar', exact: true }).click();
  check('En el panel se puede comentar', await has(panelPage.locator('.comment-body', { hasText: 'Comentario con Redis caído' })));
  await portalPage.fill('#reply', 'Respuesta con Redis caído');
  await portalPage.getByRole('button', { name: 'Responder' }).click();
  check('En el portal se puede responder', await has(portalPage.locator('.comment-body', { hasText: 'Respuesta con Redis caído' })));

  const missed = await callApi('POST', `/api/tickets/${ticket.id}/comments`, lauraJwt, { body: 'Enviado con Redis caído' });
  await sleep(800);
  check('Un cambio de otra persona no llega en vivo: no hay avisos', missed.status === 201 && await portalPage.locator('.comment-body', { hasText: 'Enviado con Redis caído' }).count() === 0);

  console.log('--- 3. Vuelve Redis ---');
  docker('start', 'helpdesk-redis-1');
  redisStopped = false;
  check('El aviso desaparece solo en el portal', await gone(portalPage.locator('.live-offline')));
  check('El aviso desaparece solo en el panel', await gone(panelPage.getByTestId('live-offline')));
  check('El portal recupera lo que se perdió, sin recargar', await has(portalPage.locator('.comment-body', { hasText: 'Enviado con Redis caído' })));
  await sleep(6000); // que la conexión quede estable

  const liveAgain = await callApi('POST', `/api/tickets/${ticket.id}/comments`, lauraJwt, { body: 'Con Redis de vuelta' });
  check('Los avisos en vivo vuelven al portal', liveAgain.status === 201 && await has(portalPage.locator('.comment-body', { hasText: 'Con Redis de vuelta' })));
  const liveAgainPanel = await callApi('POST', `/api/tickets/${pedroTicket.id}/comments`, lauraJwt, { body: 'Con Redis de vuelta en el panel' });
  check('Los avisos en vivo vuelven al panel', liveAgainPanel.status === 201 && await has(panelPage.locator('.comment-body', { hasText: 'Con Redis de vuelta en el panel' })));
} finally {
  if (redisStopped) docker('start', 'helpdesk-redis-1');
  await browser.close();
  const cleaner = await jwt('laura@helpdesk.local').catch(() => lauraJwt);
  for (const id of created) await callApi('DELETE', `/api/tickets/${id}`, cleaner).catch(() => {});
  apiState.child.kill();
  bff.child.kill();
  panel.child.kill();
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
