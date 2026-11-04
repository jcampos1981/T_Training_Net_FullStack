// La aplicación orquestada por Aspire: un ticket creado en el portal tiene que verse en el dashboard como una sola traza, hasta el worker.
// Uso: tests/e2e/run.ps1 -Suite aspire -NoBuild  (por dentro: node aspire.mjs <deps> <capturas>    Contraseña en HELPDESK_DEV_PASSWORD.)
//   run.ps1 detiene Compose, levanta el AppHost, y deja todo como estaba. El dashboard no pide login: solo se publica en esta máquina.
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [depsDir, shotsDir] = process.argv.slice(2);
const { chromium } = createRequire(path.join(depsDir, 'package.json'))('playwright-core');
const password = process.env.HELPDESK_DEV_PASSWORD;
const portal = 'https://localhost:7200';
const dashboard = 'http://localhost:15888';
const tag = `aspire-${Date.now()}`;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
const section = (title) => console.log(`--- ${title} ---`);

const telemetry = async (resource) => (await fetch(`${dashboard}/api/telemetry/${resource}`)).json();

// Formato OTLP: los spans vienen agrupados por servicio. Se aplanan, y cada uno recuerda de qué servicio es.
function flattenSpans(response) {
  return (response.data?.resourceSpans ?? []).flatMap((group) => {
    const service = group.resource?.attributes?.find((attribute) => attribute.key === 'service.name')?.value?.stringValue;
    return group.scopeSpans.flatMap((scope) => scope.spans.map((span) => ({ ...span, service })));
  });
}

function flattenLogs(response) {
  return (response.data?.resourceLogs ?? []).flatMap((group) => {
    const service = group.resource?.attributes?.find((attribute) => attribute.key === 'service.name')?.value?.stringValue;
    return group.scopeLogs.flatMap((scope) => scope.logRecords.map((record) => ({ ...record, service })));
  });
}

async function waitFor(condition, timeoutMs = 45_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await condition();
    if (value || Date.now() > deadline) return value;
    await sleep(1000);
  }
}

// El span "<evento> process" que continúa la traza de ese ticket.
const processSpanOf = async (eventType, ticketCode) => {
  const logs = flattenLogs(await telemetry('logs?limit=1000'));
  const log = logs.find((record) => record.service === 'helpdesk-worker' && (record.body?.stringValue ?? '').includes(`sobre ${ticketCode}`) && (record.body?.stringValue ?? '').includes(eventType === 'ticket.created' ? 'Recibimos' : 'Cerramos'));
  return log?.traceId ? { traceId: log.traceId, log } : null;
};
const spansOfTrace = async (traceId) => flattenSpans(await telemetry('spans?limit=2000')).filter((span) => span.traceId === traceId);

section('Lo que levantó Aspire');
const resources = await telemetry('resources');
for (const name of ['helpdesk-api', 'helpdesk-bff', 'helpdesk-admin', 'helpdesk-worker']) {
  const resource = resources.find((item) => item.name === name);
  check(`${name}: envía logs y métricas al dashboard`, resource?.hasLogs === true && resource?.hasMetrics === true);
}
const allSpans = flattenSpans(await telemetry('spans?limit=2000'));
check('Sin ruido de health checks', !allSpans.some((span) => /\/health/.test(span.name)));
check('Sin ruido de los trabajos en segundo plano: un SELECT suelto no es una traza', !allSpans.some((span) => !span.parentSpanId && span.name.includes('OutboxMessages')));

const browser = await chromium.launch({ channel: 'msedge', headless: true });
let ticketId;
try {
  section('Un ticket, de punta a punta');
  const customerContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
  const customer = await customerContext.newPage();
  await customer.goto(portal);
  await customer.getByRole('link', { name: 'Iniciar sesión' }).click();
  await customer.waitForURL(/localhost:8080/);
  await customer.fill('#username', 'camila');
  await customer.fill('#password', password);
  await customer.click('#kc-login');
  await customer.waitForURL((url) => url.origin === portal && !url.pathname.startsWith('/signin-oidc'));
  await customer.goto(`${portal}/tickets/new`);
  await customer.fill('#title', `${tag} impresora`);
  await customer.fill('#description', 'Probando la traza de punta a punta.');
  await customer.getByRole('button', { name: 'Abrir ticket' }).click();
  await customer.waitForURL(/\/tickets\/\d+/);
  ticketId = Number(new URL(customer.url()).pathname.split('/').pop());
  const code = `T-${String(ticketId).padStart(5, '0')}`;
  check('El portal crea el ticket', ticketId > 0, code);

  const created = await waitFor(() => processSpanOf('ticket.created', code));
  check('El worker envió el correo: el evento pasó por el outbox y RabbitMQ', Boolean(created));

  if (created) {
    const spans = await waitFor(async () => {
      const found = await spansOfTrace(created.traceId);
      return found.some((span) => span.name === 'ticket.created process') ? found : null;
    }) ?? await spansOfTrace(created.traceId);
    const services = [...new Set(spans.map((span) => span.service))].sort();
    const names = spans.map((span) => span.name);
    console.log(`      la traza ${created.traceId}: ${spans.length} spans en ${services.join(', ')}`);
    console.log(`      ${[...new Set(names)].join(' | ').slice(0, 400)}`);

    check('Una sola traza cruza el BFF, la API y el worker', ['helpdesk-api', 'helpdesk-bff', 'helpdesk-worker'].every((service) => services.includes(service)), services.join(', '));
    check('Pasa por el BFF', spans.some((span) => span.service === 'helpdesk-bff' && /POST/.test(span.name)));
    check('Lleva el nombre del caso de uso', names.includes('OpenTicket'));
    check('Muestra el SQL que corrió', spans.some((span) => span.service === 'helpdesk-api' && /INSERT/.test(span.name)));
    check('Muestra la publicación y el consumo del evento', names.includes('ticket.created publish') && names.includes('ticket.created process'));
    check('El log del worker queda en la misma traza', created.log.traceId === created.traceId);

    const texts = JSON.stringify(spans);
    check('La traza no trae el texto que escribió el cliente', !texts.includes('Probando la traza de punta a punta'));
    check('Ni su correo', !texts.includes('camila@acme.local'));
  }

  section('El dashboard');
  const panel = await customerContext.newPage();
  await panel.goto(`${dashboard}/traces`);
  await panel.waitForTimeout(3000);
  await panel.screenshot({ path: path.join(shotsDir, 'f11-trazas.png') });
  if (created) {
    await panel.goto(`${dashboard}/traces/detail/${created.traceId}`);
    // A los spans de mensajería les pone su propio nombre: "MSG rabbitmq".
    check('Muestra la traza completa, hasta el worker', await panel.getByText('INSERT worker.SentEmails', { exact: false }).first().waitFor({ timeout: 30_000 }).then(() => true, () => false));
    await panel.screenshot({ path: path.join(shotsDir, 'f11-traza.png'), fullPage: true });
  }
  await panel.goto(`${dashboard}/metrics/resource/helpdesk-api?meter=HelpDesk&instrument=helpdesk.use_case.duration&duration=5&view=Graph`);
  check('Muestra la métrica propia de los casos de uso', await panel.getByText('helpdesk.use_case.duration', { exact: false }).first().waitFor({ timeout: 30_000 }).then(() => true, () => false));
  await panel.screenshot({ path: path.join(shotsDir, 'f11-metrica.png') });

  section('Un ticket que se cierra');
  const staffContext = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'es-CL' });
  const staff = await staffContext.newPage();
  await staff.goto(portal);
  await staff.getByRole('link', { name: 'Iniciar sesión' }).click();
  await staff.waitForURL(/localhost:8080/);
  await staff.fill('#username', 'laura');
  await staff.fill('#password', password);
  await staff.click('#kc-login');
  await staff.waitForURL((url) => url.origin === portal && !url.pathname.startsWith('/signin-oidc'));
  const closed = await staff.evaluate(async (id) => {
    const headers = { 'X-CSRF': '1', 'content-type': 'application/json' };
    const ticket = await (await fetch(`/api/tickets/${id}`, { headers })).json();
    return (await fetch(`/api/tickets/${id}`, { method: 'PUT', headers, body: JSON.stringify({ title: ticket.title, priority: ticket.priority, status: 'Closed', version: ticket.version, assignedTo: ticket.assignedTo }) })).status;
  }, ticketId);
  check('La supervisora cierra el ticket', closed === 200, `${closed}`);
  check('El worker avisa al cliente', Boolean(await waitFor(() => processSpanOf('ticket.closed', code))));

  await staff.evaluate(async (id) => fetch(`/api/tickets/${id}`, { method: 'DELETE', headers: { 'X-CSRF': '1' } }), ticketId);
  ticketId = undefined;
} finally {
  await browser.close();
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
process.exit(fail === 0 ? 0 : 1);
