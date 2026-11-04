// La respuesta sugerida con modelos de verdad, en CPU: tickets cerrados que se indexan, un borrador que se va escribiendo en el panel, y qué pasa si el modelo se cae.
// Uso: tests/e2e/run.ps1 -Suite suggestions  (por dentro: node suggestions.mjs <deps> <capturas> <repo>)
//   Contraseñas en HELPDESK_DEV_PASSWORD y MSSQL_SA, conexiones en HELPDESK_SQL y HELPDESK_RABBITMQ. run.ps1 levanta Ollama y la API; esto, el panel y el worker.
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir, repo] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const admin = 'https://localhost:7300';
const api = 'http://localhost:5159';
const worker = 'http://localhost:5401';
const ollamaContainer = 'helpdesk-ollama-1';
const noMatchAnswer = 'No encontré casos parecidos. Conviene revisar este ticket a mano.';
const tag = `sugerencias-${Date.now()}`;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
const section = (title) => console.log(`--- ${title} ---`);

function sql(query) {
  return execFileSync('docker', ['exec', '-e', `SQLCMDPASSWORD=${process.env.MSSQL_SA}`, 'helpdesk-sqlserver-1', '/opt/mssql-tools18/bin/sqlcmd',
    '-S', 'localhost', '-U', 'sa', '-C', '-I', '-d', 'HelpDesk', '-h', '-1', '-W', '-Q', `SET NOCOUNT ON; ${query}`], { encoding: 'utf8' }).trim();
}

const processes = [];
function startProcess(name, url, environment, isReady) {
  const log = { text: '' };
  const child = spawn(path.join(repo, `src/${name}/bin/Debug/net10.0/${name}.exe`), [], {
    cwd: path.join(repo, `src/${name}`),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: url, ...environment },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', (chunk) => { log.text += chunk; });
  child.stderr.on('data', (chunk) => { log.text += chunk; });
  processes.push(child);
  return log;
}
async function waitUntil(condition, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await condition()) return true;
    await sleep(500);
  }
  return false;
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
  const isJson = response.headers.get('content-type')?.includes('json');
  return { status: response.status, json: isJson && text ? JSON.parse(text) : null, text };
}

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL', viewport: { width: 1360, height: 1000 } });
const page = await context.newPage();
const shot = (name) => page.screenshot({ path: path.join(shotsDir, `f12-${name}.png`), fullPage: true });

const field = () => page.getByLabel('Agregar un comentario');
const suggestButton = () => page.getByRole('button', { name: 'Sugerir respuesta' });
const writingButton = () => page.getByRole('button', { name: 'Redactando…' });
const note = () => page.getByTestId('suggestion-note');
const failure = () => page.getByTestId('suggestion-error');

// Pide la sugerencia y anota cómo va creciendo el texto, hasta que termina o avisa de un problema.
async function suggest(ticketId, timeoutMs = 240_000) {
  await page.goto(`${admin}/tickets/${ticketId}`);
  await suggestButton().waitFor();
  const started = Date.now();
  await suggestButton().click();

  const lengths = new Set();
  let firstTextAt = null;
  const deadline = started + timeoutMs;
  while (Date.now() < deadline) {
    const value = await field().inputValue();
    if (value.length > 0) {
      firstTextAt ??= Date.now() - started;
      lengths.add(value.length);
    }
    if ((await note().count()) > 0 || (await failure().count()) > 0) break;
    await sleep(150);
  }

  return {
    text: await field().inputValue(),
    lengths: lengths.size,
    firstTextAt,
    totalMs: Date.now() - started,
    finished: (await note().count()) > 0,
    failure: (await failure().count()) > 0 ? await failure().innerText() : null,
    note: (await note().count()) > 0 ? await note().innerText() : null,
  };
}
const seconds = (ms) => `${(ms / 1000).toFixed(1)} s`;

let lauraJwt;
const created = [];
let ollamaStopped = false;

async function createTicket(title, comments, status) {
  const response = await callApi('POST', '/api/tickets', lauraJwt, { title: `${title} (${tag})`, priority: 'Medium', assignedTo: 'laura' });
  if (response.status !== 201) throw new Error(`No se creó el ticket: ${response.status} ${response.text}`);
  const ticket = response.json;
  created.push(ticket.id);
  for (const body of comments) {
    const comment = await callApi('POST', `/api/tickets/${ticket.id}/comments`, lauraJwt, { body });
    if (comment.status !== 201) throw new Error(`No se agregó el comentario: ${comment.status} ${comment.text}`);
  }
  if (status) await changeStatus(ticket.id, status);
  return ticket;
}
async function changeStatus(id, status) {
  const current = (await callApi('GET', `/api/tickets/${id}`, lauraJwt)).json;
  const updated = await callApi('PUT', `/api/tickets/${id}`, lauraJwt, {
    title: current.title, priority: current.priority, status, assignedTo: current.assignedTo, version: current.version,
  });
  if (updated.status !== 200) throw new Error(`No cambió el estado: ${updated.status} ${updated.text}`);
}
const commentsOf = async (id) => (await callApi('GET', `/api/tickets/${id}/comments`, lauraJwt)).json;
const indexedCount = (ids) => Number(sql(`SELECT COUNT(*) FROM dbo.TicketEmbeddings WHERE TicketId IN (${ids.join(',')})`));

let adminLog;
let workerLog;

try {
  adminLog = startProcess('HelpDesk.Admin', admin, { Suggestions__Enabled: 'true' });
  workerLog = startProcess('HelpDesk.Worker', worker, {
    Ai__Enabled: 'true',
    ConnectionStrings__HelpDesk: process.env.HELPDESK_SQL,
    ConnectionStrings__RabbitMq: process.env.HELPDESK_RABBITMQ,
    TicketEvents__RetryDelay: '00:00:05',
    TicketEvents__MaxAttempts: '20',
  });
  if (!await waitUntil(() => adminLog.text.includes('Application started'), 60_000)) throw new Error(`El panel no arrancó:\n${adminLog.text}`);
  if (!await waitUntil(() => workerLog.text.includes('Application started'), 60_000)) throw new Error(`El worker no arrancó:\n${workerLog.text}`);

  lauraJwt = await jwt('laura@helpdesk.local');

  section('Al cerrarse, un ticket queda indexado');
  const printer = await createTicket('La impresora del piso 3 no imprime', [
    'La pantalla de la impresora dice que no tiene tóner y dejó de imprimir.',
    'Se reemplazó el cartucho de tóner. Si vuelve a pasar, pide uno nuevo en bodega con el formulario de insumos.',
  ], 'Closed');
  const vpn = await createTicket('No puedo conectarme a la VPN desde mi casa', [
    'La VPN se queda en conectando y después da un error de autenticación.',
    'La contraseña de red había vencido. Se cambió en el portal de contraseñas y la VPN volvió a conectar.',
  ], 'Closed');
  const scanner = await createTicket('El escáner de la multifuncional no envía a correo', [
    'Escanear a correo da un error de autenticación.',
    'Había vencido la contraseña de la cuenta de servicio del escáner. Se actualizó en la multifuncional. Consultas a diego.rojas@cliente-ejemplo.cl o al +56 9 8765 4321.',
  ], 'Closed');
  const stillOpen = await createTicket('La impresora del piso 5 tampoco imprime', [
    'Se cambió el tambor de la impresora, pero sigue abierto: es el dato trampa.',
  ]);
  const closedIds = [printer.id, vpn.id, scanner.id];

  const started = Date.now();
  const indexed = await waitUntil(() => indexedCount(closedIds) === closedIds.length, 240_000);
  check('el worker indexa los tres tickets cerrados', indexed, seconds(Date.now() - started));
  const stored = sql(`SELECT DISTINCT CONCAT(Model, ' ', CAST(VECTORPROPERTY(Embedding, 'Dimensions') AS int)) FROM dbo.TicketEmbeddings WHERE TicketId IN (${closedIds.join(',')})`);
  check('con el vector y el modelo que usa la búsqueda', stored === 'paraphrase-multilingual 768', stored);
  check('un ticket abierto no se indexa', indexedCount([stillOpen.id]) === 0);

  section('El panel escribe un borrador');
  const question = await createTicket('Mi impresora marca falta de tóner', ['Hoy la impresora de mi piso no imprime nada, marca que le falta tóner.']);
  const commentsBefore = (await commentsOf(question.id)).length;

  await page.goto(`${admin}/`);
  await page.waitForURL(/localhost:8080/);
  await page.fill('#username', 'laura');
  await page.fill('#password', password);
  await page.click('#kc-login');
  await page.waitForURL((url) => url.origin === admin && !url.pathname.startsWith('/signin-oidc'));

  const draft = await suggest(question.id);
  await shot('borrador');
  console.log(`      Borrador: ${draft.text.replace(/\s+/g, ' ')}`);
  console.log(`      Primera palabra: ${draft.firstTextAt ? seconds(draft.firstTextAt) : '-'}. Completo: ${seconds(draft.totalMs)}.`);
  check('termina y avisa que es un borrador', draft.finished && draft.note.includes('Es un borrador'), draft.failure ?? '');
  check('el texto aparece de a poco, no todo junto', draft.lengths >= 3, `${draft.lengths} largos distintos`);
  check('usa la solución del caso parecido', /cartucho|bodega|insumos/i.test(draft.text));
  check('dice en qué tickets se basó, con enlace', (await note().getByRole('link', { name: `T-${String(printer.id).padStart(5, '0')}` }).getAttribute('href')) === `/tickets/${printer.id}`);
  check('no cita el ticket que sigue abierto', !draft.note.includes(`T-${String(stillOpen.id).padStart(5, '0')}`) && !/tambor/i.test(draft.text));
  check('no envía nada solo', (await commentsOf(question.id)).length === commentsBefore);

  const edited = `${draft.text} Avísame si se repite.`;
  await field().fill(edited);
  await field().blur();
  await page.getByRole('button', { name: 'Comentar' }).click();
  check('el agente lo edita, y recién ahí se envía', await waitUntil(async () => (await commentsOf(question.id)).some((comment) => comment.body === edited), 15_000));

  section('Sin casos parecidos, lo dice y no llama al modelo');
  const coffee = await createTicket('La cafetera del tercer piso gotea', ['Sale agua por debajo de la cafetera.']);
  const none = await suggest(coffee.id, 60_000);
  check('responde que no encontró casos', none.text === noMatchAnswer, none.text.slice(0, 120));
  check('y no cita ninguna fuente', none.finished && !none.note.includes('Basado en'));

  section('Datos personales de otros clientes');
  const privateData = await createTicket('El escáner da error de autenticación al enviar a correo', ['No puedo escanear a correo desde la multifuncional.']);
  const masked = await suggest(privateData.id);
  console.log(`      Borrador: ${masked.text.replace(/\s+/g, ' ')}`);
  check('encuentra el caso', masked.finished && masked.note.includes(`T-${String(scanner.id).padStart(5, '0')}`));
  check('no aparece el correo ni el teléfono del otro cliente', !/diego|cliente-ejemplo|8765/i.test(masked.text));

  section('Un cliente no puede pedir una sugerencia');
  const customer = await callApi('POST', `/api/tickets/${question.id}/suggested-reply`, await jwt('camila@acme.local'));
  check('camila recibe 403', customer.status === 403, `${customer.status}`);

  section('El modelo se cae');
  execFileSync('docker', ['stop', ollamaContainer], { stdio: 'ignore' });
  ollamaStopped = true;

  const down = await suggest(question.id, 90_000);
  await shot('modelo-caido');
  check('el panel avisa, sin romperse', down.failure?.includes('no está disponible') ?? false, `${down.failure} en ${seconds(down.totalMs)}`);
  check('y no deja texto a medias', down.text === '', down.text.slice(0, 80));

  const closedDuringOutage = await createTicket('El monitor externo no da imagen', ['La pantalla queda negra y dice sin señal.', 'El cable HDMI estaba dañado y se cambió.'], 'Closed');
  await sleep(8_000);
  check('un ticket cerrado mientras tanto no se indexa todavía', indexedCount([closedDuringOutage.id]) === 0);

  execFileSync('docker', ['start', ollamaContainer], { stdio: 'ignore' });
  ollamaStopped = false;
  check('al volver el modelo, los reintentos lo indexan', await waitUntil(() => indexedCount([closedDuringOutage.id]) === 1, 180_000));

  const back = await suggest(question.id);
  check('y el panel vuelve a redactar', back.finished && back.text.length > 0, back.failure ?? '');

  section('Límite por usuario');
  let limited = false;
  for (let i = 0; i < 8 && !limited; i++) {
    const response = await callApi('POST', `/api/tickets/${coffee.id}/suggested-reply`, lauraJwt);
    limited = response.status === 429;
  }
  check('muchas seguidas reciben 429', limited);
  const tooMany = await suggest(coffee.id, 30_000);
  check('y el panel lo explica', tooMany.failure?.includes('muchas sugerencias') ?? false, tooMany.failure ?? '');
} catch (error) {
  check('la suite terminó sin errores', false, error.stack ?? String(error));
  await shot('error').catch(() => {});
  console.log(`      url=${page.url()}`);
} finally {
  if (ollamaStopped) execFileSync('docker', ['start', ollamaContainer], { stdio: 'ignore' });
  await browser.close();
  for (const child of processes) child.kill();
  for (const log of [adminLog, workerLog]) {
    const problems = (log?.text ?? '').split('\n').filter((line) => /^fail:|Unhandled/i.test(line)).slice(-5);
    if (problems.length > 0) console.log(problems.join('\n'));
  }
  if (lauraJwt) {
    const deleted = [];
    for (const id of created) deleted.push((await callApi('DELETE', `/api/tickets/${id}`, lauraJwt)).status);
    console.log(`--- Limpieza: ${created.length} tickets borrados (status ${[...new Set(deleted)].join(',')}) ---`);
    if (created.length > 0) check('al borrar un ticket se borra su vector', indexedCount(created) === 0);
  }
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
