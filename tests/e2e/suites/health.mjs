// Prueba de los health checks (Fase 7, Bloque 4): se apagan de verdad SQL Server, Redis, Keycloak y la API.
// Uso: tests/e2e/run.ps1 -Suite health  (por dentro: node health.mjs <repo>    Contraseña en HELPDESK_DEV_PASSWORD. Levanta y detiene la API, el BFF y el panel.)
import { execFileSync, spawn } from 'node:child_process';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [repo] = process.argv.slice(2);
const password = process.env.HELPDESK_DEV_PASSWORD;
const api = 'http://localhost:5159';
const bff = 'https://localhost:7200';
const admin = 'https://localhost:7300';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
function start(project, url, extraEnv = {}) {
  const state = { log: '' };
  state.child = spawn(path.join(repo, `src/${project}/bin/Debug/net10.0/${project}.exe`), [], {
    cwd: path.join(repo, `src/${project}`),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: url, ...extraEnv },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  state.child.stdout.on('data', (chunk) => { state.log += chunk; });
  state.child.stderr.on('data', (chunk) => { state.log += chunk; });
  return state;
}
const started = async (state, what) => {
  for (let i = 0; i < 240 && !state.log.includes('Application started'); i++) await sleep(250);
  if (!state.log.includes('Application started')) throw new Error(`${what} no arrancó:\n${state.log.slice(-1500)}`);
};
const docker = (...args) => execFileSync('docker', args, { stdio: 'pipe' }).toString().trim();
const healthy = (container) => docker('inspect', '-f', '{{.State.Health.Status}}', container) === 'healthy';
const waitFor = async (condition, ms = 10_000) => {
  const begin = Date.now();
  while (Date.now() - begin < ms) { if (await condition()) return true; await sleep(200); }
  return condition();
};

async function get(url, token) {
  const begin = Date.now();
  const response = await fetch(url, { redirect: 'manual', headers: token ? { authorization: `Bearer ${token}` } : {} });
  const text = await response.text();
  return { status: response.status, text, ms: Date.now() - begin, cacheControl: response.headers.get('cache-control'), json: () => JSON.parse(text) };
}
const statusIs = (url, text) => async () => (await get(url).catch(() => ({ text: '' }))).text === text;
const checkOf = (details, name) => details.json().checks.find((entry) => entry.name === name);

let apiState;
let bffState;
let adminState;
const stopped = new Set();
const stop = (container) => { docker('stop', container); stopped.add(container); };
const resume = (container) => { docker('start', container); stopped.delete(container); };

try {
  apiState = start('HelpDesk.Api', api, { SlaMonitor__CheckInterval: '00:00:02' });
  bffState = start('HelpDesk.Bff', bff);
  adminState = start('HelpDesk.Admin', admin);
  await Promise.all([started(apiState, 'La API'), started(bffState, 'El BFF'), started(adminState, 'El panel')]);

  const token = async (email) => (await (await fetch(`${api}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) })).json()).accessToken;
  const lauraJwt = await token('laura@helpdesk.local');
  const pedroJwt = await token('pedro@helpdesk.local');
  await waitFor(statusIs(`${api}/health/ready`, 'Healthy'), 20_000);

  console.log('--- 1. Todo arriba ---');
  const live = await get(`${api}/health/live`);
  const ready = await get(`${api}/health/ready`);
  check('/health/live: 200 y Healthy, sin credenciales', live.status === 200 && live.text === 'Healthy', live.text);
  check('/health/ready: 200 y Healthy, sin credenciales', ready.status === 200 && ready.text === 'Healthy', `${ready.ms} ms`);
  check('La respuesta no revela qué se revisa', !/database|redis|sla|sql/i.test(ready.text));
  check('No se guarda en caché', /no-store/.test(ready.cacheControl ?? ''), ready.cacheControl);

  console.log('--- 2. El detalle es solo para supervisión ---');
  check('Sin credenciales: 401', (await get(`${api}/health/details`)).status === 401);
  check('Un agente: 403', (await get(`${api}/health/details`, pedroJwt)).status === 403);
  const details = await get(`${api}/health/details`, lauraJwt);
  check('Supervisión ve cada revisión', details.status === 200 && ['database', 'redis', 'sla-monitor'].every((name) => checkOf(details, name)?.status === 'Healthy'), details.text.slice(0, 220));

  console.log('--- 3. Un orquestador pregunta seguido ---');
  const probes = await Promise.all(Array.from({ length: 300 }, () => get(`${api}/health/live`)));
  check('300 preguntas seguidas: ninguna rechazada por el límite de peticiones', probes.every((probe) => probe.status === 200), `${probes.filter((probe) => probe.status === 429).length} con 429`);
  check('No ensucian el log', !apiState.log.includes('/health/live'));

  console.log('--- 4. BFF y panel ---');
  for (const [name, base] of [['BFF', bff], ['Panel', admin]]) {
    const liveApp = await get(`${base}/health/live`);
    check(`${name}: /health/live, sin pedir login`, liveApp.status === 200 && liveApp.text === 'Healthy', `${liveApp.status} ${liveApp.text.slice(0, 40)}`);
    check(`${name}: /health/ready, con todo arriba`, await waitFor(statusIs(`${base}/health/ready`, 'Healthy'), 8000), (await get(`${base}/health/ready`)).text);
  }

  console.log('--- 5. Redis se cae: la API queda degradada, y sigue atendiendo ---');
  stop('helpdesk-redis-1');
  const degraded = await get(`${api}/health/ready`);
  check('/health/ready: 200 y Degraded, sin demora', degraded.status === 200 && degraded.text === 'Degraded' && degraded.ms < 1000, `${degraded.status} ${degraded.text}, ${degraded.ms} ms`);
  const whichDown = await get(`${api}/health/details`, lauraJwt);
  check('El detalle dice que es Redis', checkOf(whichDown, 'redis')?.status === 'Degraded' && checkOf(whichDown, 'database')?.status === 'Healthy', whichDown.text.slice(0, 220));
  check('El detalle no muestra el texto de la excepción', checkOf(whichDown, 'redis')?.description === null && !/localhost|6379|Redis(Connection|Timeout)/.test(whichDown.text));
  check('/health/live no se entera', (await get(`${api}/health/live`)).text === 'Healthy');
  resume('helpdesk-redis-1');
  check('Redis vuelve: otra vez Healthy, sin reiniciar', await waitFor(statusIs(`${api}/health/ready`, 'Healthy'), 40_000));

  console.log('--- 6. SQL Server se cae: la API sale del tráfico, pero no pide que la reinicien ---');
  stop('helpdesk-sqlserver-1');
  const down = await get(`${api}/health/ready`);
  check('/health/ready: 503 y Unhealthy, antes de 4 s', down.status === 503 && down.text === 'Unhealthy' && down.ms < 4000, `${down.status} ${down.text}, ${down.ms} ms`);
  const liveDown = await get(`${api}/health/live`);
  check('/health/live sigue en 200, y al instante: reiniciar no arreglaría la base', liveDown.status === 200 && liveDown.ms < 300, `${liveDown.status}, ${liveDown.ms} ms`);
  check('Pasadas 3 vueltas sin terminar, el monitor de SLA aparece degradado', await waitFor(async () => checkOf(await get(`${api}/health/details`, lauraJwt), 'sla-monitor')?.status === 'Degraded', 30_000));
  const whichSql = await get(`${api}/health/details`, lauraJwt);
  check('El detalle responde 503, y dice que es la base', whichSql.status === 503 && checkOf(whichSql, 'database')?.status === 'Unhealthy', whichSql.text.slice(0, 260));
  check('Sin datos de la conexión', !/1433|Password|Server=|sqlserver/i.test(whichSql.text));
  check('Dependiendo de la API, el BFF sigue Healthy: /health/live de la API responde', (await get(`${bff}/health/ready`)).text === 'Healthy');
  resume('helpdesk-sqlserver-1');
  await waitFor(() => healthy('helpdesk-sqlserver-1'), 180_000);
  check('SQL Server vuelve: otra vez Healthy, sin reiniciar', await waitFor(statusIs(`${api}/health/ready`, 'Healthy'), 60_000));

  console.log('--- 7. La API se cae: BFF y panel quedan degradados, y no salen del tráfico ---');
  apiState.child.kill();
  await sleep(500);
  for (const [name, base] of [['BFF', bff], ['Panel', admin]]) {
    const appDown = await get(`${base}/health/ready`);
    check(`${name}: 200 y Degraded`, appDown.status === 200 && appDown.text === 'Degraded', `${appDown.status} ${appDown.text}, ${appDown.ms} ms`);
    check(`${name}: /health/live no se entera`, (await get(`${base}/health/live`)).text === 'Healthy');
  }
  apiState = start('HelpDesk.Api', api);
  await started(apiState, 'La API');
  check('Vuelve la API: BFF otra vez Healthy', await waitFor(statusIs(`${bff}/health/ready`, 'Healthy'), 10_000));
  check('Vuelve la API: panel otra vez Healthy', await waitFor(statusIs(`${admin}/health/ready`, 'Healthy'), 10_000));

  console.log('--- 8. Keycloak se cae ---');
  stop('helpdesk-keycloak-1');
  check('BFF: Degraded', (await get(`${bff}/health/ready`)).text === 'Degraded');
  check('Panel: Degraded', (await get(`${admin}/health/ready`)).text === 'Degraded');
  check('La API no depende de Keycloak para atender: Healthy', (await get(`${api}/health/ready`)).text === 'Healthy');
  resume('helpdesk-keycloak-1');
  await waitFor(() => healthy('helpdesk-keycloak-1'), 240_000);
  check('Keycloak vuelve: BFF otra vez Healthy', await waitFor(statusIs(`${bff}/health/ready`, 'Healthy'), 15_000));
  check('Keycloak vuelve: panel otra vez Healthy', await waitFor(statusIs(`${admin}/health/ready`, 'Healthy'), 15_000));

  console.log('--- 9. Una API sin Redis configurado ---');
  const noRedis = start('HelpDesk.Api', 'http://localhost:5161', { ConnectionStrings__Redis: '' });
  try {
    await started(noRedis, 'La API sin Redis');
    const jwtNoRedis = lauraJwt;
    const detailsNoRedis = await get('http://localhost:5161/health/details', jwtNoRedis);
    check('No revisa Redis, y está Healthy', detailsNoRedis.status === 200 && detailsNoRedis.json().status === 'Healthy' && !checkOf(detailsNoRedis, 'redis'), detailsNoRedis.text.slice(0, 200));
    const dashboard = await get('http://localhost:5161/api/dashboard', jwtNoRedis);
    check('El dashboard funciona solo con la memoria', dashboard.status === 200);
  } finally {
    noRedis.child.kill();
  }
} finally {
  for (const container of stopped) docker('start', container);
  apiState?.child.kill();
  bffState?.child.kill();
  adminState?.child.kill();
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
