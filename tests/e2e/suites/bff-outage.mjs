// Una caída de Keycloak no debe cerrar las sesiones del BFF (Fase 7). Se detiene el contenedor de verdad.
// Uso: tests/e2e/run.ps1 -Suite bff-outage  (por dentro: node bff-outage.mjs <deps> <repo>    Contraseña en HELPDESK_DEV_PASSWORD. La API debe estar corriendo.)
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const portal = 'https://localhost:7200';
const tokenLifetimeMs = 300_000;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

let bffLog = '';
// Renovar faltando 4:58: la renovación queda pendiente desde el segundo 2, sin esperar 4 minutos.
const bff = spawn(path.join(repo, 'src/HelpDesk.Bff/bin/Debug/net10.0/HelpDesk.Bff.exe'), [], {
  cwd: path.join(repo, 'src/HelpDesk.Bff'),
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: portal, Keycloak__RefreshBeforeExpiry: '00:04:58' },
  stdio: ['ignore', 'pipe', 'pipe'],
});
bff.stdout.on('data', (chunk) => { bffLog += chunk; });
bff.stderr.on('data', (chunk) => { bffLog += chunk; });
for (let i = 0; i < 120 && !bffLog.includes('Application started'); i++) await sleep(250);

const keycloak = (action) => execFileSync('docker', [action, 'helpdesk-keycloak-1'], { stdio: 'pipe' });
async function keycloakHealthy() {
  for (let i = 0; i < 240; i++) {
    const status = execFileSync('docker', ['inspect', '-f', '{{.State.Health.Status}}', 'helpdesk-keycloak-1']).toString().trim();
    if (status === 'healthy') return true;
    await sleep(1000);
  }
  return false;
}

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
const page = await context.newPage();
const callThroughBff = (url) => page.evaluate(async (target) => {
  const response = await fetch(target, { headers: { 'X-CSRF': '1' } });
  const text = await response.text();
  return { status: response.status, retryAfter: response.headers.get('retry-after'), text };
}, url);

let keycloakStopped = false;
try {
  await page.goto(portal);
  await page.getByRole('link', { name: 'Iniciar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', 'camila');
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL((url) => url.origin === portal && !url.pathname.startsWith('/signin-oidc'));
  const loginAt = Date.now();
  await page.locator('table.tickets').waitFor();

  await sleep(3000);
  const healthy = await callThroughBff('/api/portal/tickets');
  check('Con Keycloak arriba, la renovación funciona', healthy.status === 200 && /Access token renovado/.test(bffLog), `${healthy.status}`);

  keycloak('stop');
  keycloakStopped = true;
  console.log('      Keycloak detenido');
  await sleep(3000);

  // ---------- Token todavía vigente: es el bug que se corrige ----------
  const during = await callThroughBff('/api/portal/tickets');
  check('Keycloak caído y token vigente: la API sigue respondiendo', during.status === 200, `${during.status}`);
  const user = await callThroughBff('/bff/user');
  check('La sesión NO se cerró', user.status === 200, `${user.status}`);
  check('El BFF registra la caída como una advertencia', /Keycloak no responde; la sesión sigue/.test(bffLog));
  check('La cookie de sesión sigue en el navegador', (await context.cookies(portal)).some((cookie) => cookie.name.startsWith('__Host-helpdesk')));

  // ---------- Token vencido: 503, no 401 ----------
  const wait = tokenLifetimeMs - (Date.now() - loginAt) + 5000;
  console.log(`      esperando que venza el token (${Math.round(wait / 1000)} s)...`);
  await sleep(wait);
  const expired = await callThroughBff('/api/portal/tickets');
  check('Token vencido y Keycloak caído: 503 con Retry-After, no 401', expired.status === 503 && expired.retryAfter === '15', `${expired.status} Retry-After=${expired.retryAfter}`);
  check('El 503 explica el problema', /sesión sigue activa/.test(expired.text), expired.text.slice(0, 120));
  const stillThere = await callThroughBff('/bff/user');
  check('La sesión sigue existiendo', stillThere.status === 200, `${stillThere.status}`);
  await page.reload();
  check('El portal no pide iniciar sesión', await page.getByRole('link', { name: 'Cerrar sesión' }).waitFor({ timeout: 8000 }).then(() => true).catch(() => false));

  // ---------- Keycloak vuelve ----------
  keycloak('start');
  keycloakStopped = false;
  console.log('      Keycloak iniciado; esperando que esté sano...');
  check('Keycloak volvió', await keycloakHealthy());
  let recovered = { status: 0 };
  for (let i = 0; i < 30 && recovered.status !== 200; i++) {
    recovered = await callThroughBff('/api/portal/tickets');
    if (recovered.status !== 200) await sleep(2000);
  }
  check('Al volver Keycloak, la misma sesión funciona sin login', recovered.status === 200 && (await callThroughBff('/bff/user')).status === 200, `${recovered.status}`);
} finally {
  if (keycloakStopped) {
    keycloak('start');
    await keycloakHealthy();
  }
  await browser.close();
  bff.kill();
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
