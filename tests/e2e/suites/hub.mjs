// Prueba del hub de avisos directamente contra la API (Fase 7, Bloque 1): quién recibe qué, y quién no puede conectarse.
// Uso: tests/e2e/run.ps1 -Suite hub  (por dentro: node hub.mjs <raíz-del-repo>    Contraseña en HELPDESK_DEV_PASSWORD. API corriendo con Jwt__AccessTokenMinutes=1.)
import { createRequire } from 'node:module';
import path from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [repo] = process.argv.slice(2);
const signalR = createRequire(path.join(repo, 'tests/e2e/package.json'))('@microsoft/signalr');
const { WebSocket: Ws } = { WebSocket: createRequire(path.join(repo, 'tests/e2e/package.json'))('ws') };
const api = 'http://localhost:5159';
const hubUrl = `${api}/hubs/tickets`;
const password = process.env.HELPDESK_DEV_PASSWORD;

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}

async function jwt(email) {
  const response = await fetch(`${api}/api/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
  if (!response.ok) throw new Error(`token ${email}: ${response.status}`);
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

// Un cliente que anota todo lo que recibe.
async function listen(token) {
  const received = [];
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, { accessTokenFactory: () => token, transport: signalR.HttpTransportType.WebSockets })
    .configureLogging(signalR.LogLevel.None)
    .build();
  for (const method of ['TicketChanged', 'MyTicketChanged', 'TicketsReassigned']) {
    connection.on(method, (payload) => received.push({ method, payload }));
  }
  let closedAt = null;
  connection.onclose(() => { closedAt = Date.now(); });
  await connection.start();
  return { connection, received, closedAt: () => closedAt, clear: () => received.splice(0, received.length) };
}
const waitFor = async (condition, ms = 4000) => {
  const start = Date.now();
  while (Date.now() - start < ms) { if (condition()) return true; await sleep(50); }
  return condition();
};
const ofTicket = (client, id) => client.received.filter((entry) => entry.payload.ticketId === id);

const created = [];
const tag = `hub-${Date.now()}`;
let lauraToken;

try {
  // ---------- Quién puede conectarse ----------
  const anonymous = await fetch(`${hubUrl}/negotiate?negotiateVersion=1`, { method: 'POST' });
  check('Sin credenciales: negotiate responde 401', anonymous.status === 401, `${anonymous.status}`);

  const invalid = await fetch(`${hubUrl}/negotiate?negotiateVersion=1`, { method: 'POST', headers: { authorization: 'Bearer abc.def.ghi' } });
  check('Con un token inválido: 401', invalid.status === 401, `${invalid.status}`);

  // La cookie de Identity sirve para la API, pero no para el hub (Cross-Site WebSocket Hijacking).
  const cookieLogin = await fetch(`${api}/api/auth/login?useCookies=true`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: 'laura@helpdesk.local', password }),
  });
  const cookie = (cookieLogin.headers.getSetCookie?.() ?? []).map((line) => line.split(';')[0]).join('; ');
  const withCookieApi = await fetch(`${api}/api/me`, { headers: { cookie } });
  check('La cookie de Identity sí sirve para la API', cookie.length > 0 && withCookieApi.status === 200, `${withCookieApi.status}`);
  const withCookieHub = await fetch(`${hubUrl}/negotiate?negotiateVersion=1`, { method: 'POST', headers: { cookie } });
  check('La cookie de Identity NO abre el hub (403)', withCookieHub.status === 403, `${withCookieHub.status}`);

  const withCookieSocket = await new Promise((resolve) => {
    const socket = new Ws(`ws://localhost:5159/hubs/tickets`, { headers: { cookie, origin: 'http://localhost:9999' } });
    socket.on('open', () => { socket.close(); resolve('abierto'); });
    socket.on('unexpected-response', (_, response) => resolve(String(response.statusCode)));
    socket.on('error', () => resolve('error'));
  });
  check('Un WebSocket con esa cookie, desde otra página, se rechaza', withCookieSocket === '403', withCookieSocket);

  // ---------- Quién recibe qué ----------
  lauraToken = await jwt('laura@helpdesk.local');
  const pedroToken = await jwt('pedro@helpdesk.local');
  const luisToken = await jwt('luis@helpdesk.local');
  const camilaToken = await jwt('camila@acme.local');
  const diegoToken = await jwt('diego@acme.local');
  const startedAt = Date.now();

  const [laura, pedro, luis, camila, diego] = await Promise.all([lauraToken, pedroToken, luisToken, camilaToken, diegoToken].map(listen));
  const everyone = [laura, pedro, luis, camila, diego];
  const clearAll = () => everyone.forEach((client) => client.clear());

  // 1. Un supervisor crea un ticket asignado a pedro.
  const internal = await callApi('POST', '/api/tickets', lauraToken, { title: `${tag} interno`, priority: 'Medium', assignedTo: 'pedro' });
  created.push(internal.json.id);
  await waitFor(() => ofTicket(pedro, internal.json.id).length > 0);
  await sleep(300);
  const pedroGot = ofTicket(pedro, internal.json.id)[0];
  check('Crear: el agente asignado recibe el aviso', pedroGot?.method === 'TicketChanged' && pedroGot.payload.kind === 'Created', JSON.stringify(pedroGot?.payload));
  check('Crear: el enum viaja como texto, y el aviso trae quién lo hizo', pedroGot?.payload.status === 'Open' && pedroGot.payload.actor === 'laura');
  check('Crear: supervisión lo recibe', ofTicket(laura, internal.json.id).length === 1);
  check('Crear: otro agente NO lo recibe', ofTicket(luis, internal.json.id).length === 0);
  check('Crear: los clientes NO lo reciben', ofTicket(camila, internal.json.id).length === 0 && ofTicket(diego, internal.json.id).length === 0);
  check('Un ticket interno no genera aviso de cliente', everyone.every((client) => client.received.every((entry) => entry.method !== 'MyTicketChanged')));
  clearAll();

  // 2. El agente comenta.
  await callApi('POST', `/api/tickets/${internal.json.id}/comments`, pedroToken, { body: 'hola' });
  await waitFor(() => ofTicket(laura, internal.json.id).length > 0);
  await sleep(200);
  check('Comentar: supervisión recibe Commented, firmado por pedro',
    ofTicket(laura, internal.json.id)[0]?.payload.kind === 'Commented' && ofTicket(laura, internal.json.id)[0]?.payload.actor === 'pedro');
  check('Comentar: otro agente NO lo recibe', ofTicket(luis, internal.json.id).length === 0);
  clearAll();

  // 3. Lo que NO se guardó no se avisa.
  const ticket = (await callApi('GET', `/api/tickets/${internal.json.id}`, lauraToken)).json;
  const stale = await callApi('PUT', `/api/tickets/${internal.json.id}`, lauraToken, { title: 'versión antigua', priority: 'Medium', status: 'Open', assignedTo: 'pedro', version: '12345' });
  const invalidUpdate = await callApi('PUT', `/api/tickets/${internal.json.id}`, lauraToken, { title: '', priority: 'Medium', status: 'Open', assignedTo: 'pedro', version: ticket.version });
  await sleep(500);
  check('Un conflicto (409) y un 400 no generan avisos', stale.status === 409 && invalidUpdate.status === 400 && everyone.every((client) => client.received.length === 0), `${stale.status}/${invalidUpdate.status}`);

  // 4. Reasignar: se enteran el que lo tenía y el que lo recibe. Sin distinguir mayúsculas, como SQL Server.
  const moved = await callApi('PUT', `/api/tickets/${internal.json.id}`, lauraToken, { title: ticket.title, priority: ticket.priority, status: ticket.status, assignedTo: 'LUIS', version: ticket.version });
  await waitFor(() => ofTicket(luis, internal.json.id).length > 0 && ofTicket(pedro, internal.json.id).length > 0);
  check('Reasignar: lo recibe el nuevo agente, aunque esté escrito "LUIS"', ofTicket(luis, internal.json.id)[0]?.payload.kind === 'Reassigned', `${moved.status}`);
  check('Reasignar: lo recibe quien lo tenía antes', ofTicket(pedro, internal.json.id)[0]?.payload.kind === 'Reassigned');
  check('Reasignar: supervisión lo recibe una sola vez', ofTicket(laura, internal.json.id).length === 1);
  clearAll();

  // 5. Un cliente abre un ticket desde el portal.
  const portal = await callApi('POST', '/api/portal/tickets', camilaToken, { title: `${tag} portal`, priority: 'Medium', description: 'descripción' });
  created.push(portal.json.id);
  await waitFor(() => ofTicket(camila, portal.json.id).length > 0 && ofTicket(laura, portal.json.id).length > 0);
  await sleep(200);
  const camilaGot = ofTicket(camila, portal.json.id)[0];
  check('Abrir en el portal: el cliente recibe MyTicketChanged, marcado como suyo', camilaGot?.method === 'MyTicketChanged' && camilaGot.payload.bySupport === false, JSON.stringify(camilaGot?.payload));
  check('Abrir en el portal: supervisión lo recibe sin asignar', ofTicket(laura, portal.json.id)[0]?.payload.assignedTo === null && ofTicket(laura, portal.json.id)[0]?.payload.kind === 'Created');
  check('Abrir en el portal: otro cliente NO lo recibe', ofTicket(diego, portal.json.id).length === 0);
  check('Abrir en el portal: un agente sin relación NO lo recibe', ofTicket(pedro, portal.json.id).length === 0);
  clearAll();

  // 6. Asignarlo: solo cambia el agente, y eso al cliente no le incumbe.
  const portalTicket = (await callApi('GET', `/api/tickets/${portal.json.id}`, lauraToken)).json;
  const assigned = await callApi('PUT', `/api/tickets/${portal.json.id}`, lauraToken, { title: portalTicket.title, priority: portalTicket.priority, status: portalTicket.status, assignedTo: 'pedro', version: portalTicket.version });
  await waitFor(() => ofTicket(pedro, portal.json.id).length > 0);
  await sleep(300);
  check('Asignar: el agente se entera', ofTicket(pedro, portal.json.id)[0]?.payload.kind === 'Reassigned');
  check('Asignar: el cliente NO recibe nada (no es parte de su contrato)', ofTicket(camila, portal.json.id).length === 0);
  clearAll();

  // 7. Soporte responde.
  await callApi('POST', `/api/tickets/${portal.json.id}/comments`, pedroToken, { body: 'respuesta de soporte' });
  await waitFor(() => ofTicket(camila, portal.json.id).length > 0);
  const reply = ofTicket(camila, portal.json.id)[0];
  check('Responder: el cliente lo recibe como de soporte', reply?.payload.kind === 'Commented' && reply.payload.bySupport === true);
  check('El aviso del cliente no revela agente ni autor', reply && !('actor' in reply.payload) && !('assignedTo' in reply.payload), Object.keys(reply?.payload ?? {}).join(','));
  check('Responder: otro cliente NO lo recibe', ofTicket(diego, portal.json.id).length === 0);
  clearAll();

  // 8. El cliente responde: el agente asignado se entera.
  await callApi('POST', `/api/portal/tickets/${portal.json.id}/comments`, camilaToken, { body: 'respuesta del cliente' });
  await waitFor(() => ofTicket(pedro, portal.json.id).length > 0 && ofTicket(camila, portal.json.id).length > 0);
  check('El cliente responde: el agente lo recibe, firmado por camila', ofTicket(pedro, portal.json.id)[0]?.payload.actor === 'camila');
  check('El cliente responde: su propio aviso no viene como de soporte', ofTicket(camila, portal.json.id)[0]?.payload.bySupport === false);
  clearAll();

  // 9. Cambio de estado: el cliente sí se entera.
  const current = (await callApi('GET', `/api/tickets/${portal.json.id}`, lauraToken)).json;
  await callApi('PUT', `/api/tickets/${portal.json.id}`, lauraToken, { title: current.title, priority: current.priority, status: 'InProgress', assignedTo: 'luis', version: current.version });
  await waitFor(() => ofTicket(camila, portal.json.id).length > 0 && ofTicket(pedro, portal.json.id).length > 0);
  check('Cambiar estado y agente a la vez: el cliente recibe Updated', ofTicket(camila, portal.json.id)[0]?.payload.kind === 'Updated' && ofTicket(camila, portal.json.id)[0]?.payload.status === 'InProgress');
  check('Cambiar estado y agente a la vez: quien lo tenía también se entera', ofTicket(pedro, portal.json.id).length === 1);
  clearAll();

  // 10. Reasignación masiva: un solo aviso.
  const fromAgent = `e2e-${tag}`;
  const bulk = [];
  for (let i = 0; i < 3; i++) {
    const response = await callApi('POST', '/api/tickets', lauraToken, { title: `${tag} masivo ${i}`, priority: 'Low', assignedTo: fromAgent });
    bulk.push(response.json.id);
    created.push(response.json.id);
  }
  await sleep(200);
  clearAll();
  const reassigned = await callApi('POST', `/api/agents/${fromAgent}/reassign`, lauraToken, { to: 'pedro' });
  await waitFor(() => pedro.received.some((entry) => entry.method === 'TicketsReassigned'));
  await sleep(200);
  const bulkEvents = pedro.received.filter((entry) => entry.method === 'TicketsReassigned');
  check('Masiva: un solo aviso con la cantidad', bulkEvents.length === 1 && bulkEvents[0].payload.tickets === 3 && bulkEvents[0].payload.to === 'pedro', JSON.stringify(bulkEvents[0]?.payload));
  check('Masiva: supervisión lo recibe, y el resto no', laura.received.some((entry) => entry.method === 'TicketsReassigned') && luis.received.length === 0 && camila.received.length === 0, `${reassigned.status}`);
  clearAll();
  const none = await callApi('POST', `/api/agents/nadie-${tag}/reassign`, lauraToken, { to: 'pedro' });
  await sleep(300);
  check('Masiva sin tickets que mover: sin aviso', none.status === 200 && laura.received.length === 0, `${none.status}`);

  // 11. Eliminar: el cliente se entera.
  await callApi('DELETE', `/api/tickets/${portal.json.id}`, lauraToken);
  created.splice(created.indexOf(portal.json.id), 1);
  await waitFor(() => ofTicket(camila, portal.json.id).length > 0 && ofTicket(luis, portal.json.id).length > 0);
  check('Eliminar: el cliente y el agente lo reciben', ofTicket(camila, portal.json.id)[0]?.payload.kind === 'Deleted' && ofTicket(luis, portal.json.id)[0]?.payload.kind === 'Deleted');

  // ---------- Los clientes no pueden invocar nada ----------
  let invokeError = '';
  try { await camila.connection.invoke('OnConnectedAsync'); } catch (error) { invokeError = error.message; }
  check('El hub no expone métodos: invocar falla', invokeError.length > 0, invokeError.slice(0, 90));
  check('Después de ese error, la conexión sigue viva', camila.connection.state === 'Connected');

  // ---------- Al vencer el token, la conexión se cierra ----------
  const remaining = 60_000 - (Date.now() - startedAt);
  console.log(`      esperando el vencimiento del token (~${Math.max(0, Math.round(remaining / 1000))} s)...`);
  const closed = await waitFor(() => camila.closedAt() !== null, remaining + 45_000);
  const lifetime = camila.closedAt() === null ? null : Math.round((camila.closedAt() - startedAt) / 1000);
  check('La conexión se cierra cuando vence el token, sin que el cliente haga nada', closed && lifetime >= 50, `a los ${lifetime} s de token de 60 s`);

  // JwtBearer tolera 30 s de diferencia de reloj después de "exp": recién ahí lo rechaza.
  await sleep(32_000);
  let refused = '';
  try { await listen(camilaToken); } catch (error) { refused = error.message; }
  check('Con el token vencido ya no se puede volver a conectar', refused.includes('401'), refused.slice(0, 90));

  await Promise.all(everyone.map((client) => client.connection.stop()));
} finally {
  const cleaner = await jwt('laura@helpdesk.local').catch(() => lauraToken);
  for (const id of created) await callApi('DELETE', `/api/tickets/${id}`, cleaner).catch(() => {});
}

console.log(`\nTOTAL: ${pass} OK, ${fail} con error`);
process.exit(fail ? 1 : 0);
