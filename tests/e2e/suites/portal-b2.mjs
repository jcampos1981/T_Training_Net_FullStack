// Prueba de punta a punta del portal en Edge (headless): Vite -> BFF -> Keycloak -> API.
// Uso: tests/e2e/run.ps1 -Suite portal-b2  (por dentro: node portal-b2.mjs <carpeta-de-dependencias> <carpeta-de-capturas>   (contraseña en HELPDESK_DEV_PASSWORD))
import { createRequire } from 'node:module';
import path from 'node:path';

const [depsDir, shotsDir] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const portal = 'http://localhost:5173';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
const page = await context.newPage();
const consoleErrors = [];
// El navegador registra como error toda respuesta 4xx/5xx. Un 401 de /bff/user sin sesión es esperado (la app lo
// usa para saber que no hay sesión); cualquier otro error cuenta.
page.on('console', (message) => {
  if (message.type() !== 'error') return;
  const url = message.location().url ?? '';
  if (!url.startsWith(portal)) return; // errores de la página de Keycloak (su favicon), no de la app
  if (message.text().includes('401') && url.endsWith('/bff/user')) return;
  consoleErrors.push(`${message.text()} @ ${url}`);
});

async function login(user) {
  await page.getByRole('link', { name: 'Iniciar sesión' }).click();
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', user);
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL(`${portal}/`);
}

try {
  await page.goto(`${portal}/`);
  await page.getByRole('heading', { name: 'Soporte para clientes' }).waitFor();
  check('sin sesion: pantalla de bienvenida con "Iniciar sesion"', true);

  await login('camila');
  await page.getByRole('heading', { name: 'Mis tickets' }).waitFor();
  const rows = await page.locator('table.tickets tbody tr').count();
  const firstRow = await page.locator('table.tickets tbody tr').first().innerText();
  check('camila: login por Keycloak y vuelta al portal con sus tickets', rows === 3, `filas=${rows} | ${firstRow.replace(/\s+/g, ' ')}`);
  const header = await page.locator('.user').innerText();
  check('el encabezado muestra su email', header.includes('camila@acme.local'), header.replace(/\s+/g, ' '));
  await page.screenshot({ path: path.join(shotsDir, 'portal-camila.png'), fullPage: true });

  const cookies = await context.cookies(portal);
  const session = cookies.filter((c) => c.name.startsWith('__Host-helpdesk'));
  check('cookie de sesion HttpOnly, Secure y SameSite=Strict en http://localhost:5173',
    session.length > 0 && session.every((c) => c.httpOnly && c.secure && c.sameSite === 'Strict'),
    session.map((c) => `${c.name} httpOnly=${c.httpOnly} secure=${c.secure} sameSite=${c.sameSite}`).join(' | '));
  const visibleToJs = await page.evaluate(() => document.cookie);
  check('JavaScript no puede leer la cookie (document.cookie)', !visibleToJs.includes('__Host-helpdesk'), `document.cookie="${visibleToJs}"`);

  await page.goto(`${portal}/no-existe`);
  await page.getByRole('heading', { name: 'No encontramos esa página' }).waitFor();
  check('ruta desconocida: pantalla 404 de React Router', true);

  await page.goto(`${portal}/`);
  await page.getByRole('link', { name: 'Cerrar sesión' }).click();
  await page.getByRole('heading', { name: 'Soporte para clientes' }).waitFor();
  check('cerrar sesion: vuelve a la bienvenida', true, page.url());

  await login('pedro');
  await page.getByText('Esta cuenta no es de cliente').waitFor();
  check('pedro (Agent) inicia sesion pero ve "Esta cuenta no es de cliente"', true);
  await page.screenshot({ path: path.join(shotsDir, 'portal-pedro.png'), fullPage: true });

  check('sin errores en la consola del navegador', consoleErrors.length === 0, consoleErrors.join(' | '));
} catch (error) {
  fail++;
  console.log(`FAIL  excepcion: ${error.message.split('\n')[0]}`);
  await page.screenshot({ path: path.join(shotsDir, 'portal-error.png'), fullPage: true });
} finally {
  await browser.close();
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
