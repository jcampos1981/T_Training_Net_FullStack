// HelpDesk en el clúster local (deploy/k8s/install.ps1), con varias réplicas, en Edge headless.
// Uso: tests/e2e/run.ps1 -Suite kubernetes -NoBuild  (por dentro: node kubernetes.mjs <deps> <capturas> <repo>    Contraseña en HELPDESK_DEV_PASSWORD.)
//   La suite mata pods, detiene SQL Server y Redis, y genera carga. Deja el clúster como lo encontró. Tarda unos 15 minutos.
import { execFile, execFileSync, spawnSync } from 'node:child_process';
import https from 'node:https';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const portal = 'https://portal.localhost:8443';
const panel = 'https://panel.localhost:8443';
const tag = `k8s-${Date.now()}`;
const app = (name) => `app.kubernetes.io/name=${name}`;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
const section = (title) => console.log(`--- ${title} ---`);
const info = (text) => console.log(`      ${text}`);

const env = { ...process.env, KUBECONFIG: path.join(repo, 'deploy/k8s/.kube/kubeconfig.yaml') };
const kubectl = (...args) => execFileSync('kubectl', ['-n', 'helpdesk', ...args], { encoding: 'utf8', env, maxBuffer: 1 << 26, stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const attemptKubectl = (...args) => spawnSync('kubectl', ['-n', 'helpdesk', ...args], { encoding: 'utf8', env, maxBuffer: 1 << 26 });
const json = (...args) => JSON.parse(kubectl(...args, '-o', 'json'));
// Sin bloquear, por lo mismo que rolloutDone.
const helm = (...args) => new Promise((resolve) => {
  execFile('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'deploy/k8s/helm.ps1'), ...args], { encoding: 'utf8', maxBuffer: 1 << 24 }, (error, stdout, stderr) => {
    resolve({ status: error ? 1 : 0, stdout, stderr });
  });
});
const has = (locator, timeout = 20_000) => locator.first().waitFor({ timeout }).then(() => true, () => false);

async function waitUntil(condition, timeoutMs, intervalMs = 2000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await condition()) return true;
    await sleep(intervalMs);
  }
  return false;
}

const podsOf = (name) => json('get', 'pods', '-l', app(name)).items.filter((pod) => !pod.metadata.deletionTimestamp);
const isReady = (pod) => pod.status.conditions?.some((condition) => condition.type === 'Ready' && condition.status === 'True') ?? false;
const restartsOf = (name) => podsOf(name).reduce((total, pod) => total + (pod.status.containerStatuses ?? []).reduce((sum, container) => sum + container.restartCount, 0), 0);
// Sin bloquear: el tráfico que mide los cortes corre en este mismo proceso. Con una llamada síncrona, se detendría durante todo el rollout.
const rolloutDone = (kind, timeout = '5m') => new Promise((resolve) => {
  execFile('kubectl', ['-n', 'helpdesk', 'rollout', 'status', kind, `--timeout=${timeout}`], { env }, (error) => resolve(!error));
});

// Una petición por el Ingress, con una conexión TCP nueva: Traefik la puede mandar a cualquier pod.
function request(host, urlPath, headers = {}, method = 'GET') {
  return new Promise((resolve) => {
    const started = Date.now();
    const call = https.request(
      { host: '127.0.0.1', port: 8443, path: urlPath, method, servername: host, rejectUnauthorized: false, agent: false, timeout: 15_000, headers: { host: `${host}:8443`, ...headers } },
      (response) => {
        response.resume();
        response.on('end', () => resolve({ status: response.statusCode, ms: Date.now() - started }));
      });
    call.on('timeout', () => { call.destroy(); resolve({ status: 0, error: 'timeout', ms: Date.now() - started }); });
    call.on('error', (error) => resolve({ status: 0, error: error.code ?? error.message, ms: Date.now() - started }));
    call.end();
  });
}

// Tráfico continuo mientras pasa algo. Cuenta lo que un usuario vería como una falla.
function keepTraffic(calls) {
  const results = [];
  let running = true;
  const workers = calls.map(async ({ run, delayMs }) => {
    while (running) {
      results.push(await run());
      await sleep(delayMs);
    }
  });
  return {
    async stop() {
      running = false;
      await Promise.all(workers);
      const failures = results.filter((result) => result.status === 0 || result.status >= 500);
      return { total: results.length, ok: results.filter((result) => result.status === 200 && result.api).length, failures, slowest: Math.max(...results.map((result) => result.ms)) };
    },
  };
}

async function signIn(page, user) {
  await page.waitForURL(/keycloak\.localhost/);
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

section('El clúster');
const deployments = json('get', 'deployments').items;
for (const name of ['api', 'bff', 'admin']) {
  const deployment = deployments.find((item) => item.metadata.name === name);
  check(`${name}: al menos 2 réplicas listas`, (deployment?.status.readyReplicas ?? 0) >= 2, `${deployment?.status.readyReplicas} listas`);
  const pod = podsOf(name)[0];
  const container = pod.spec.containers[0];
  check(`${name}: no root, disco de solo lectura y sin capacidades`, pod.spec.securityContext.runAsNonRoot === true && container.securityContext.readOnlyRootFilesystem === true && container.securityContext.capabilities.drop.includes('ALL'));
  check(`${name}: las tres probes`, Boolean(container.startupProbe && container.livenessProbe && container.readinessProbe));
  check(`${name}: requests y limits`, Boolean(container.resources.requests?.cpu && container.resources.limits?.memory));
  check(`${name}: variables y secretos desde ConfigMap y Secret`, container.envFrom?.[0]?.configMapRef?.name === name && container.env.every((variable) => variable.valueFrom?.secretKeyRef));
}
check('Las migraciones terminaron antes de instalar', json('get', 'job', 'migrator').status.succeeded === 1);
check('Autoescalado en la API y el BFF', ['api', 'bff'].every((name) => json('get', 'hpa', name).spec.maxReplicas > 2));
check('El autoescalado ya ve el consumo de CPU', await waitUntil(() => json('get', 'hpa', 'api').status.currentMetrics?.[0]?.resource?.current?.averageUtilization !== undefined, 180_000, 5000));
const restartsAtStart = { api: restartsOf('api'), bff: restartsOf('bff'), admin: restartsOf('admin') };

const logLines = kubectl('logs', `deployment/api`, '--tail=30').split('\n').filter(Boolean);
check('Los logs salen como JSON, uno por línea', logLines.length > 0 && logLines.every((line) => { try { return Boolean(JSON.parse(line).Timestamp ?? JSON.parse(line).EventId !== undefined); } catch { return false; } }), `${logLines.length} líneas`);

section('Gateway API');
const route = json('get', 'httproute', 'bff').status?.parents?.[0]?.conditions ?? [];
check('Traefik aceptó el HTTPRoute', route.some((condition) => condition.type === 'Accepted' && condition.status === 'True'));
const throughGateway = spawnSync('curl.exe', ['-s', '-o', 'NUL', '-w', '%{http_code}', '--resolve', 'portal-gateway.localhost:8088:127.0.0.1', 'http://portal-gateway.localhost:8088/health/live'], { encoding: 'utf8' }).stdout;
check('El BFF responde a través del Gateway', throughGateway === '200', throughGateway);

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const created = [];

try {
  const customerContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1200, height: 800 } });
  const customer = await customerContext.newPage();
  const comments = customer.locator('.comment-body');

  section('Portal y panel por el Ingress, con TLS');
  await signInToPortal(customer, 'camila');
  check('Inicia sesión: el BFF arma la redirect_uri con https, detrás de Traefik', new URL(customer.url()).origin === portal);
  const cookies = await customerContext.cookies(portal);
  const session = cookies.filter((cookie) => cookie.name.startsWith('__Host-helpdesk'));
  check('La cookie de sesión es Secure', session.length > 0 && session.every((cookie) => cookie.secure));
  // ASP.NET Core parte una cookie grande en varias: hay que mandarlas todas.
  const sessionCookie = { cookie: session.map((cookie) => `${cookie.name}=${cookie.value}`).join('; ') };

  await customer.goto(`${portal}/tickets/new`);
  await customer.fill('#title', `${tag} impresora`);
  await customer.fill('#description', 'Probando en Kubernetes.');
  await customer.getByRole('button', { name: 'Abrir ticket' }).click();
  await customer.waitForURL(/\/tickets\/\d+/);
  const ticketId = Number(new URL(customer.url()).pathname.split('/').pop());
  created.push(ticketId);
  check('Crea el ticket', ticketId > 0, `#${ticketId}`);

  const staffContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1400, height: 900 } });
  const staff = await staffContext.newPage();
  await staff.goto(`${panel}/tickets/${ticketId}`);
  await signIn(staff, 'laura');
  await staff.waitForURL((url) => url.origin === panel && !url.pathname.startsWith('/signin-oidc'));
  const commentField = staff.getByLabel('Agregar un comentario', { exact: true });
  check('El panel abre el ticket: pasó el callback de OpenID Connect con varias réplicas', await has(commentField));
  check('El panel usa afinidad: Traefik le puso su cookie', (await staffContext.cookies(panel)).some((cookie) => cookie.name === 'helpdesk-route'));
  const comment = async (text) => {
    await commentField.fill(text);
    await commentField.press('Tab');
    await staff.getByRole('button', { name: 'Comentar', exact: true }).click();
  };

  section('Avisos entre réplicas: el backplane de Redis');
  const openedPerPod = () => Object.fromEntries(podsOf('api').filter(isReady).map((pod) => [pod.metadata.name, (kubectl('logs', pod.metadata.name).match(/Conexi[^ ]+ al hub abierta/g) ?? []).length]));
  const tabs = [customer];
  const openedBefore = openedPerPod();
  const openedSince = () => Object.entries(openedPerPod()).map(([pod, count]) => count - (openedBefore[pod] ?? 0));
  // Cada pestaña abre un WebSocket nuevo, y el Service lo manda a cualquier réplica. Se abren hasta que haya conexiones nuevas en las dos.
  await waitUntil(async () => {
    if (openedSince().filter((count) => count > 0).length >= 2) return true;
    if (tabs.length >= 12) return true;
    const tab = await customerContext.newPage();
    await tab.goto(`${portal}/tickets/${ticketId}`);
    tabs.push(tab);
    await sleep(800);
    return false;
  }, 90_000, 200);
  const connections = openedSince();
  check('Las pestañas del cliente quedaron en réplicas distintas de la API', connections.filter((count) => count > 0).length >= 2, JSON.stringify(connections));
  await comment('Comentario para todas las réplicas.');
  const received = await Promise.all(tabs.map((tab) => has(tab.locator('.comment-body', { hasText: 'Comentario para todas las réplicas.' }))));
  check('Todas las pestañas lo reciben, estén en la réplica que estén', received.every(Boolean), `${received.filter(Boolean).length} de ${tabs.length}`);
  await Promise.all(tabs.slice(1).map((tab) => tab.close()));
  await staff.screenshot({ path: path.join(shotsDir, 'f10-panel.png') });

  section('Las llaves de Data Protection, compartidas');
  const reads = await Promise.all(Array.from({ length: 12 }, () => request('portal.localhost', '/bff/user', sessionCookie)));
  check('La misma cookie vale en todas las réplicas del BFF', reads.every((result) => result.status === 200), JSON.stringify([...new Set(reads.map((result) => result.status))]));

  section('Apagado ordenado');
  // Todos: el WebSocket del cliente está en alguno. Sin el corte al recibir SIGTERM, ese pod tardaría más de 30 segundos en irse.
  const startedDelete = Date.now();
  kubectl('delete', 'pod', '-l', app('bff'));
  const deleteSeconds = (Date.now() - startedDelete) / 1000;
  check('Los pods terminan solos al recibir SIGTERM, aun con WebSockets abiertos', deleteSeconds >= 5 && deleteSeconds < 20, `${deleteSeconds.toFixed(1)} s, con 5 s de preStop`);
  check('Con todos los pods del BFF reemplazados', await waitUntil(() => { const pods = podsOf('bff'); return pods.length >= 2 && pods.every(isReady); }, 120_000));
  // Traefik se entera un momento después de que los pods están listos.
  await waitUntil(async () => (await request('portal.localhost', '/')).status === 200, 30_000, 500);
  await customer.reload();
  check('La sesión del cliente sobrevive: las llaves están en Redis', await has(comments.filter({ hasText: 'Comentario para todas las réplicas.' })), (await customer.innerText('body')).replace(/\s+/g, ' ').slice(0, 70));
  check('Y no le pide iniciar sesión otra vez', await customer.getByRole('link', { name: 'Iniciar sesión' }).count() === 0);

  section('Se actualiza el panel con un agente trabajando');
  const expectLoad = () => staff.waitForEvent('load', { timeout: 150_000 }).then(() => true, () => false);
  let reloaded = expectLoad();
  kubectl('rollout', 'restart', 'deployment/admin');
  check('Actualización normal: la pestaña se recarga sola', await reloaded);
  check('Y no cae en una página de error', await has(commentField, 30_000), (await staff.innerText('body')).replace(/\s+/g, ' ').slice(0, 60));
  check('Entra sin pedir contraseña', await staff.locator('#username').count() === 0);
  await rolloutDone('deployment/admin');
  await comment('Comentario después de actualizar el panel.');
  check('El comentario llega al portal', await has(comments.filter({ hasText: 'Comentario después de actualizar el panel.' }), 30_000));

  section('Se van todos los pods del panel a la vez');
  reloaded = expectLoad();
  for (const pod of podsOf('admin')) kubectl('delete', 'pod', pod.metadata.name, '--wait=false');
  check('La pestaña se recarga sola', await reloaded);
  check('Y no cae en una página de error', await has(commentField, 30_000), (await staff.innerText('body')).replace(/\s+/g, ' ').slice(0, 60));
  await rolloutDone('deployment/admin');

  section('Actualización sin cortes');
  const traffic = keepTraffic([
    { run: () => request('portal.localhost', '/'), delayMs: 50 },
    { run: () => request('portal.localhost', '/'), delayMs: 50 },
    // Por el BFF hasta la API y la base. Poco: el límite por usuario es de 120 por minuto.
    { run: async () => ({ ...await request('portal.localhost', '/api/portal/tickets', { ...sessionCookie, 'x-csrf': '1' }), api: true }), delayMs: 800 },
  ]);
  kubectl('rollout', 'restart', 'deployment/api', 'deployment/bff');
  const rolled = await rolloutDone('deployment/api') && await rolloutDone('deployment/bff');
  await sleep(3000);
  const update = await traffic.stop();
  check('El rollout termina', rolled);
  check('El tráfico llegó de verdad hasta la API', update.ok > 10, `${update.ok} respuestas correctas`);
  check('Ninguna petición falló durante la actualización', update.failures.length === 0, `${update.failures.length} de ${update.total}, ${JSON.stringify(update.failures.slice(0, 3))}. La más lenta: ${update.slowest} ms`);

  section('Una versión que no arranca');
  const imageBefore = json('get', 'deployment', 'bff').spec.template.spec.containers[0].image;
  const brokenTraffic = keepTraffic([{ run: () => request('portal.localhost', '/'), delayMs: 100 }]);
  const brokenUpgrade = await helm('upgrade', 'helpdesk', '/charts/helpdesk', '-n', 'helpdesk', '--reuse-values', '--set', 'apps.bff.image=helpdesk-bff-no-existe', '--wait', '--timeout', '40s');
  check('Helm marca la actualización como fallida', brokenUpgrade.status !== 0);
  check('Las réplicas anteriores siguen listas', podsOf('bff').filter(isReady).length >= 2);
  const rollback = await helm('rollback', 'helpdesk', '-n', 'helpdesk', '--wait', '--timeout', '5m');
  check('helm rollback vuelve a la versión anterior', rollback.status === 0 && json('get', 'deployment', 'bff').spec.template.spec.containers[0].image === imageBefore, (rollback.stderr ?? '').slice(0, 160));
  check('El rollout termina', await rolloutDone('deployment/bff'));
  const broken = await brokenTraffic.stop();
  check('Nadie notó nada', broken.failures.length === 0, `${broken.failures.length} de ${broken.total}`);

  section('Helm: una migración que falla no toca las aplicaciones');
  const failedUpgrade = await helm('upgrade', 'helpdesk', '/charts/helpdesk', '-n', 'helpdesk', '--reuse-values', '--set', 'image.tag=no-existe', '--wait', '--timeout', '40s');
  check('La actualización falla en el Job de migración', failedUpgrade.status !== 0 && /migrator|hook|pre-upgrade/i.test(failedUpgrade.stderr + failedUpgrade.stdout));
  check('Las aplicaciones siguen con su versión', json('get', 'deployment', 'bff').spec.template.spec.containers[0].image === imageBefore);
  attemptKubectl('delete', 'job', 'migrator', '--wait=true');
  const repaired = await helm('upgrade', 'helpdesk', '/charts/helpdesk', '-n', 'helpdesk', '--reuse-values', '--set', `image.tag=${imageBefore.split(':').pop()}`, '--wait', '--timeout', '5m');
  check('Volver a la versión buena funciona', repaired.status === 0, (repaired.stderr ?? '').slice(0, 200));

  section('Readiness y liveness: sin base de datos');
  const apiRestarts = restartsOf('api');
  kubectl('scale', 'statefulset/sqlserver', '--replicas=0');
  check('La API sale del tráfico: /health/ready falla', await waitUntil(() => podsOf('api').every((pod) => !isReady(pod)), 120_000));
  check('Pero no se reinicia: reiniciar no arregla una base caída', restartsOf('api') === apiRestarts, `${restartsOf('api')} reinicios`);
  const withoutDatabase = await request('portal.localhost', '/api/portal/tickets', { ...sessionCookie, 'x-csrf': '1' });
  check('El BFF responde un error, no se queda colgado', withoutDatabase.status >= 500, `${withoutDatabase.status} en ${withoutDatabase.ms} ms`);
  const bffHealth = await request('portal.localhost', '/health/ready');
  check('El BFF sigue en el tráfico: su dependencia caída no lo saca', bffHealth.status === 200, `${bffHealth.status}`);
  kubectl('scale', 'statefulset/sqlserver', '--replicas=1');
  check('SQL Server vuelve', await rolloutDone('statefulset/sqlserver', '6m'));
  check('La API vuelve al tráfico sola', await waitUntil(() => podsOf('api').every(isReady), 180_000));
  await customer.reload();
  check('El ticket sigue ahí: el disco del StatefulSet sobrevivió', await has(comments.filter({ hasText: 'Comentario para todas las réplicas.' }), 30_000));

  section('Sin Redis');
  kubectl('scale', 'deployment/redis', '--replicas=0');
  await waitUntil(() => podsOf('redis').length === 0, 60_000);
  const withoutRedis = await Promise.all(Array.from({ length: 6 }, () => request('portal.localhost', '/bff/user', sessionCookie)));
  check('Las réplicas que ya corren siguen abriendo las cookies: tienen las llaves en memoria', withoutRedis.every((result) => result.status === 200), JSON.stringify(withoutRedis.map((result) => result.status)));
  await customer.reload();
  check('El portal sigue funcionando', await has(comments.filter({ hasText: 'Comentario para todas las réplicas.' })));
  kubectl('scale', 'deployment/redis', '--replicas=1');
  check('Redis vuelve', await rolloutDone('deployment/redis'));

  section('Autoescalado');
  const replicasBefore = json('get', 'deployment', 'api').spec.replicas;
  attemptKubectl('delete', 'pod', 'carga', '--wait=true');
  // Dentro del clúster, directo a la API: /health/ready no tiene límite de peticiones y toca la base en cada una.
  kubectl('run', 'carga', '--image=fortio/fortio:latest_release', '--restart=Never', '--', 'load', '-qps', '0', '-c', '40', '-t', '240s', 'http://api:8080/health/ready');
  let peak = replicasBefore;
  const scaledUp = await waitUntil(() => { peak = Math.max(peak, json('get', 'deployment', 'api').spec.replicas); return peak > replicasBefore; }, 200_000, 5000);
  check('Con carga, el autoescalador agrega réplicas', scaledUp, `de ${replicasBefore} a ${peak}`);
  await waitUntil(() => { const replicas = json('get', 'deployment', 'api').spec.replicas; peak = Math.max(peak, replicas); return attemptKubectl('get', 'pod', 'carga', '-o', 'jsonpath={.status.phase}').stdout !== 'Running'; }, 300_000, 5000);
  info(`máximo de réplicas: ${peak}. Consumo con carga: ${kubectl('top', 'pods', '-l', app('api'), '--no-headers').split('\n').map((line) => line.replace(/\s+/g, ' ').split(' ').slice(1).join(' ')).join(' | ')}`);
  check('Ninguna réplica murió por memoria ni se reinició', restartsOf('api') === apiRestarts, `${restartsOf('api')} reinicios`);
  attemptKubectl('delete', 'pod', 'carga', '--wait=false');
  check('Sin carga, vuelve a 2 réplicas', await waitUntil(() => json('get', 'deployment', 'api').spec.replicas === 2, 420_000, 5000));

  section('Al final');
  check('Ningún pod reinició en toda la prueba', ['api', 'bff', 'admin'].every((name) => restartsOf(name) <= restartsAtStart[name]), `api ${restartsOf('api')}, bff ${restartsOf('bff')}, admin ${restartsOf('admin')}`);
} finally {
  const cleanerContext = await browser.newContext({ ignoreHTTPSErrors: true });
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
