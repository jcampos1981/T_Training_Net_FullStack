// Prueba de la caché y Redis (Fase 7, Bloque 3) contra SQL Server, Redis y dos instancias de la API.
// Uso: tests/e2e/run.ps1 -Suite cache  (por dentro: node cache.mjs <repo>    Contraseñas en HELPDESK_DEV_PASSWORD y MSSQL_SA. Levanta y detiene las APIs y detiene Redis.)
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [repo] = process.argv.slice(2);
const signalR = createRequire(path.join(repo, 'tests/e2e/package.json'))('@microsoft/signalr');
const password = process.env.HELPDESK_DEV_PASSWORD;
const api = 'http://localhost:5159';
const api2 = 'http://localhost:5160';
const tag = `cache-${Date.now()}`;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

function startApi(url, extraEnv = {}) {
  const state = { log: '' };
  state.child = spawn(path.join(repo, 'src/HelpDesk.Api/bin/Debug/net10.0/HelpDesk.Api.exe'), [], {
    cwd: path.join(repo, 'src/HelpDesk.Api'),
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

function sqlRun(statement) {
  return execFileSync('docker', ['exec', '-e', `SQLCMDPASSWORD=${process.env.MSSQL_SA}`, 'helpdesk-sqlserver-1', '/opt/mssql-tools18/bin/sqlcmd',
    '-C', '-I', '-b', '-S', 'localhost', '-U', 'sa', '-d', 'HelpDesk', '-h', '-1', '-W', '-Q', `SET NOCOUNT ON; ${statement}`], { stdio: 'pipe' }).toString().trim();
}
// Cuántas veces SQL Server ejecutó de verdad una sentencia: la prueba mira la base, no lo que la API dice que hizo.
const executions = (marker) => Number(sqlRun(`SELECT ISNULL(MAX(qs.execution_count), 0) FROM sys.dm_exec_query_stats AS qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t WHERE t.text LIKE N'%${marker}%' AND t.text NOT LIKE N'%dm_exec_query_stats%';`));
const DASHBOARD = 'Dashboard: tickets por estado';
const AGENTS = 'Resumen de carga por agente';

const redisCli = (...args) => execFileSync('docker', ['exec', 'helpdesk-redis-1', 'redis-cli', ...args], { stdio: 'pipe' }).toString().trim();
const docker = (...args) => execFileSync('docker', args, { stdio: 'pipe' }).toString().trim();

async function jwt(email, base = api) {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${base}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
    if (response.ok) return (await response.json()).accessToken;
    if (response.status !== 429 || attempt > 8) throw new Error(`token ${email}: ${response.status}`);
    await sleep(10_000);
  }
}
async function callApi(method, url, token, body, base = api) {
  const startedAt = Date.now();
  const response = await fetch(`${base}${url}`, {
    method,
    headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), ...(body ? { 'content-type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  return { status: response.status, json: text ? JSON.parse(text) : null, text, age: response.headers.get('age'), ms: Date.now() - startedAt };
}
async function listen(token, base = api) {
  const received = [];
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${base}/hubs/tickets`, { accessTokenFactory: () => token, transport: signalR.HttpTransportType.WebSockets })
    .configureLogging(signalR.LogLevel.None)
    .build();
  for (const method of ['TicketChanged', 'MyTicketChanged', 'TicketsReassigned', 'SlaAlertsRaised']) {
    connection.on(method, (payload) => received.push({ method, payload }));
  }
  await connection.start();
  return { connection, received, clear: () => received.splice(0, received.length) };
}
const waitFor = async (condition, ms = 5000) => {
  const start = Date.now();
  while (Date.now() - start < ms) { if (await condition()) return true; await sleep(50); }
  return condition();
};
const ofTicket = (client, id) => client.received.filter((entry) => entry.payload.ticketId === id);

const created = [];
let first;
let second;
let lauraJwt;
let redisStopped = false;

try {
  console.log('--- 0. Redis vacío al empezar ---');
  const foreign = redisCli('--scan').split('\n').filter((key) => key && !key.startsWith('helpdesk'));
  check('En Redis no hay nada de otra aplicación', foreign.length === 0, foreign.slice(0, 3).join(','));

  first = startApi(api);
  await started(first, 'La API');
  lauraJwt = await jwt('laura@helpdesk.local');
  const pedroJwt = await jwt('pedro@helpdesk.local');
  const camilaJwt = await jwt('camila@acme.local');
  await sleep(6000); // que venza lo que haya guardado

  console.log('--- 1. Dashboard con HybridCache: 50 peticiones a la vez ---');
  let before = executions(DASHBOARD);
  const burst = await Promise.all(Array.from({ length: 50 }, () => callApi('GET', '/api/dashboard', lauraJwt)));
  let queries = executions(DASHBOARD) - before;
  check('Las 50 responden bien', burst.every((response) => response.status === 200));
  check('La base ejecutó la consulta UNA vez', queries === 1, `${queries} ejecuciones`);
  check('Todas reciben el mismo resultado', new Set(burst.map((response) => response.json.generatedAt)).size === 1, burst[0].json.generatedAt);
  check('El resultado se puede leer entero desde la caché', burst[0].json.ticketsByStatus.length > 0 && burst[0].json.openWorkload.length > 0);

  const slowest = Math.max(...burst.map((response) => response.ms));
  const hit = await callApi('GET', '/api/dashboard', lauraJwt);
  check('Lo guardado responde mucho más rápido', hit.json.generatedAt === burst[0].json.generatedAt && hit.ms < slowest, `${hit.ms} ms contra ${slowest} ms`);

  console.log('--- 2. Vence, y queda en Redis ---');
  const keys = redisCli('--scan', '--pattern', 'helpdesk:*');
  check('Está guardado en Redis, con prefijo', keys.split('\n').includes('helpdesk:dashboard'), keys.split('\n').join(','));
  const ttl = Number(redisCli('PTTL', 'helpdesk:dashboard'));
  check('Con un vencimiento corto', ttl > 0 && ttl <= 4000, `${ttl} ms`);
  await sleep(5500);
  before = executions(DASHBOARD);
  const refreshed = await callApi('GET', '/api/dashboard', lauraJwt);
  check('Pasados 5 s, vuelve a consultar la base', refreshed.json.generatedAt !== burst[0].json.generatedAt && executions(DASHBOARD) - before === 1, refreshed.json.generatedAt);

  console.log('--- 3. Un cambio se ve a los pocos segundos ---');
  const openBefore = refreshed.json.ticketsByStatus.find((row) => row.status === 'Open').total;
  const portal = await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `${tag} portal`, priority: 'Medium', description: 'Descripción de prueba.' });
  created.push(portal.json.id);
  await sleep(6000); // lo que espera el panel después de un aviso
  const afterChange = await callApi('GET', '/api/dashboard', lauraJwt);
  check('La recarga del panel (6 s después) ya trae el cambio', afterChange.json.ticketsByStatus.find((row) => row.status === 'Open').total === openBefore + 1, `${openBefore} → ${openBefore + 1}`);

  console.log('--- 4. Permisos con la caché llena ---');
  check('Un agente recibe 403, aunque esté guardado', (await callApi('GET', '/api/dashboard', pedroJwt)).status === 403);
  check('Sin credenciales, 401', (await callApi('GET', '/api/dashboard')).status === 401);

  console.log('--- 5. La primera petición se cancela y las demás siguen ---');
  await sleep(5500);
  const controller = new AbortController();
  const abandoned = fetch(`${api}/api/dashboard`, { headers: { authorization: `Bearer ${lauraJwt}` }, signal: controller.signal }).catch(() => 'cancelada');
  await sleep(15);
  const waiters = Array.from({ length: 5 }, () => callApi('GET', '/api/dashboard', lauraJwt));
  await sleep(15);
  controller.abort();
  const afterAbort = await Promise.all(waiters);
  await abandoned;
  check('Las que esperaban reciben su respuesta', afterAbort.every((response) => response.status === 200), afterAbort.map((response) => response.status).join(','));
  check('Sin errores por el DbContext de la petición cancelada', !/ObjectDisposed|disposed/.test(first.log));

  console.log('--- 6. /api/agents con output caching ---');
  await sleep(5500);
  before = executions(AGENTS);
  const miss = await callApi('GET', '/api/agents', lauraJwt);
  const agentsHit = await callApi('GET', '/api/agents', lauraJwt);
  check('La primera va a la base, sin header Age', miss.status === 200 && miss.age === null, `${miss.ms} ms`);
  check('La segunda sale de la caché: header Age, y el mismo cuerpo', agentsHit.age !== null && agentsHit.text === miss.text, `Age=${agentsHit.age}, ${agentsHit.ms} ms`);
  check('La base ejecutó la consulta una vez', executions(AGENTS) - before === 1, `${executions(AGENTS) - before}`);

  check('Con la caché llena, un agente recibe 403 y no los datos', (await callApi('GET', '/api/agents', pedroJwt)).status === 403);
  const anonymousHit = await callApi('GET', '/api/agents');
  check('Con la caché llena, sin credenciales recibe 401', anonymousHit.status === 401 && !anonymousHit.text.includes('openTickets'), `${anonymousHit.status}`);
  const customerHit = await callApi('GET', '/api/agents', camilaJwt);
  check('Con la caché llena, un cliente recibe 403', customerHit.status === 403, `${customerHit.status}`);

  await sleep(5500);
  check('Pasados 5 s, se vuelve a consultar', (await callApi('GET', '/api/agents', lauraJwt)).age === null);

  await sleep(5500);
  before = executions(AGENTS);
  const agentsBurst = await Promise.all(Array.from({ length: 30 }, () => callApi('GET', '/api/agents', lauraJwt)));
  check('30 a la vez: una sola consulta', agentsBurst.every((response) => response.status === 200) && executions(AGENTS) - before === 1, `${executions(AGENTS) - before}`);

  console.log('--- 7. Reasignar vacía la caché de inmediato ---');
  const fromAgent = `e2e-${tag}`;
  const bulk = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} masivo`, priority: 'Low', assignedTo: fromAgent });
  created.push(bulk.json.id);
  await sleep(5500);
  const listBefore = await callApi('GET', '/api/agents', lauraJwt);
  check('El agente aparece con su ticket', listBefore.json.some((agent) => agent.name === fromAgent && agent.openTickets === 1));
  check('Y ese resultado queda guardado', (await callApi('GET', '/api/agents', lauraJwt)).age !== null);
  const moved = await callApi('POST', `/api/agents/${fromAgent}/reassign`, lauraJwt, { to: 'pedro' });
  const listAfter = await callApi('GET', '/api/agents', lauraJwt);
  check('Quien reasigna ve el resultado enseguida, no lo guardado', moved.status === 200 && listAfter.age === null && !listAfter.json.some((agent) => agent.name === fromAgent), `${moved.status}, Age=${listAfter.age}`);

  console.log('--- 8. Una respuesta de error no se guarda ---');
  await sleep(5500);
  const denied = await callApi('GET', '/api/agents', pedroJwt);
  const allowed = await callApi('GET', '/api/agents', lauraJwt);
  check('El 403 de un agente no le llega a supervisión', denied.status === 403 && allowed.status === 200 && allowed.age === null);

  console.log('--- 9. Dos instancias: comparten HybridCache, y SignalR cruza entre ellas ---');
  second = startApi(api2);
  await started(second, 'La segunda API');
  await sleep(5500);
  before = executions(DASHBOARD);
  const onFirst = await callApi('GET', '/api/dashboard', lauraJwt, undefined, api);
  const onSecond = await callApi('GET', '/api/dashboard', lauraJwt, undefined, api2);
  check('La segunda instancia usa lo que guardó la primera en Redis', onSecond.status === 200 && onSecond.json.generatedAt === onFirst.json.generatedAt && executions(DASHBOARD) - before === 1, `${executions(DASHBOARD) - before} consulta, ${onFirst.ms} ms y ${onSecond.ms} ms`);

  const pedroOn2 = await listen(pedroJwt, api2);
  const camilaOn2 = await listen(camilaJwt, api2);
  const lauraOn1 = await listen(lauraJwt, api);
  const pedroOn1 = await listen(pedroJwt, api);
  const crossing = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} cruza`, priority: 'Medium', assignedTo: 'pedro' }, api);
  created.push(crossing.json.id);
  check('Sale de la instancia 1 y llega a quien está en la 2', await waitFor(() => ofTicket(pedroOn2, crossing.json.id).length > 0), JSON.stringify(ofTicket(pedroOn2, crossing.json.id)[0]?.payload?.kind));
  await waitFor(() => ofTicket(pedroOn1, crossing.json.id).length > 0 && ofTicket(lauraOn1, crossing.json.id).length > 0);
  await sleep(300);
  check('Exactamente una vez en cada conexión', ofTicket(pedroOn2, crossing.json.id).length === 1 && ofTicket(pedroOn1, crossing.json.id).length === 1 && ofTicket(lauraOn1, crossing.json.id).length === 1);

  const camilaReply = await callApi('POST', `/api/tickets/${portal.json.id}/comments`, lauraJwt, { body: 'Respuesta desde la instancia 1' }, api);
  check('Un cliente en la instancia 2 recibe su aviso', camilaReply.status === 201 && await waitFor(() => ofTicket(camilaOn2, portal.json.id).length > 0));

  const otherAgentTicket = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} de luis`, priority: 'Medium', assignedTo: 'luis' }, api);
  created.push(otherAgentTicket.json.id);
  await waitFor(() => ofTicket(lauraOn1, otherAgentTicket.json.id).length > 0);
  await sleep(300);
  check('Los grupos se respetan entre instancias: pedro no recibe lo de luis', ofTicket(pedroOn2, otherAgentTicket.json.id).length === 0);
  check('Tampoco un cliente', ofTicket(camilaOn2, otherAgentTicket.json.id).length === 0);

  const fromSecond = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} desde la 2`, priority: 'Medium', assignedTo: 'pedro' }, api2);
  created.push(fromSecond.json.id);
  check('También en el otro sentido: de la 2 a la 1', await waitFor(() => ofTicket(pedroOn1, fromSecond.json.id).length === 1));

  console.log('--- 10. Redis se cae ---');
  const lauraOn1Second = lauraOn1;
  docker('stop', 'helpdesk-redis-1');
  redisStopped = true;
  await sleep(1500);
  await sleep(5500); // que venza lo que había en memoria
  const dashDown = await callApi('GET', '/api/dashboard', lauraJwt);
  check('El dashboard sigue respondiendo', dashDown.status === 200, `${dashDown.ms} ms`);
  check('Sin demora apreciable por Redis', dashDown.ms < 1500, `${dashDown.ms} ms`);
  const ticketDown = (await callApi('GET', `/api/tickets/${crossing.json.id}`, lauraJwt)).json;
  const putDown = await callApi('PUT', `/api/tickets/${crossing.json.id}`, lauraJwt, { title: ticketDown.title, priority: 'High', status: ticketDown.status, assignedTo: 'pedro', version: ticketDown.version });
  check('Guardar sigue funcionando, aunque el aviso no salga', putDown.status === 200, `${putDown.status}`);
  check('Y sin demora por el aviso que falló', putDown.ms < 1000, `${putDown.ms} ms`);
  const commentDown = await callApi('POST', `/api/tickets/${crossing.json.id}/comments`, pedroJwt, { body: 'Con Redis caído' });
  check('Comentar sigue funcionando', commentDown.status === 201 && commentDown.ms < 1000, `${commentDown.ms} ms`);
  const agentsDown = await callApi('GET', '/api/agents', lauraJwt);
  check('El output caching no depende de Redis', agentsDown.status === 200, `${agentsDown.ms} ms`);
  const agentsDown2 = await callApi('GET', '/api/agents', lauraJwt);
  check('Y sigue guardando en memoria', agentsDown2.age !== null, `Age=${agentsDown2.age}`);
  check('La API no registra errores sin atrapar', !/^fail: Microsoft\.AspNetCore\.Diagnostics/m.test(first.log), '');

  let connectDown = 'conectó';
  try { const extra = await listen(pedroJwt, api); await extra.connection.stop(); } catch (error) { connectDown = `falló: ${error.message.slice(0, 60)}`; }
  console.log(`      una conexión nueva al hub con Redis caído: ${connectDown}`);

  console.log('--- 11. Sin Redis desde el arranque ---');
  const third = startApi('http://localhost:5161');
  try {
    await started(third, 'La tercera API');
    check('La API arranca igual', true);
    const dashThird = await callApi('GET', '/api/dashboard', lauraJwt, undefined, 'http://localhost:5161');
    check('Y responde', dashThird.status === 200, `${dashThird.ms} ms`);
  } finally {
    third.child.kill();
  }

  console.log('--- 12. Redis vuelve ---');
  docker('start', 'helpdesk-redis-1');
  redisStopped = false;
  await waitFor(() => { try { return redisCli('PING') === 'PONG'; } catch { return false; } }, 30_000);

  // SignalR se vuelve a suscribir solo: se prueba con avisos que crucen entre instancias.
  let healed = false;
  for (let attempt = 0; attempt < 12 && !healed; attempt++) {
    const probe = await callApi('POST', '/api/tickets', lauraJwt, { title: `${tag} vuelve ${attempt}`, priority: 'Low', assignedTo: 'pedro' }, api);
    created.push(probe.json.id);
    healed = await waitFor(() => ofTicket(pedroOn2, probe.json.id).length > 0, 2500);
  }
  check('Los avisos vuelven a cruzar entre instancias, sin reiniciar nada', healed);
  await sleep(5500);
  await callApi('GET', '/api/dashboard', lauraJwt);
  check('La caché vuelve a guardar en Redis', await waitFor(() => redisCli('--scan', '--pattern', 'helpdesk:*').includes('helpdesk:dashboard'), 5000));
  console.log(`      seguían conectados sin saberlo: pedro en la 2 = ${pedroOn2.connection.state}`);

  await Promise.all([pedroOn2, camilaOn2, lauraOn1Second, pedroOn1].map((client) => client.connection.stop()));
} finally {
  if (redisStopped) docker('start', 'helpdesk-redis-1');
  const cleaner = await jwt('laura@helpdesk.local').catch(() => lauraJwt);
  for (const id of created) await callApi('DELETE', `/api/tickets/${id}`, cleaner).catch(() => {});
  second?.child.kill();
  first?.child.kill();
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
