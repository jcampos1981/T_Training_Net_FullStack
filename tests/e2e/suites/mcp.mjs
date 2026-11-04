// El servidor MCP, como lo usa un asistente: un proceso con el que se conversa en JSON-RPC por stdin y stdout, contra la API de verdad.
// Uso: tests/e2e/run.ps1 -Suite mcp  (por dentro: node mcp.mjs <raíz-del-repo>    Contraseña en HELPDESK_DEV_PASSWORD.)
import { spawn } from 'node:child_process';
import path from 'node:path';
import { createInterface } from 'node:readline';

const [repo] = process.argv.slice(2);
const password = process.env.HELPDESK_DEV_PASSWORD;
const api = 'http://localhost:5159';

let pass = 0;
let fail = 0;
function check(name, ok, detail = '') {
  if (ok) { pass++; console.log(`PASS  ${name}  ${detail}`); } else { fail++; console.log(`FAIL  ${name}  ${detail}`); }
}
const section = (title) => console.log(`--- ${title} ---`);

// Un asistente conectado: arranca el proceso y le hace preguntas.
class Assistant {
  #next = 1;
  #pending = new Map();
  errors = '';
  stray = [];

  constructor(email, accountPassword = password) {
    this.process = spawn(path.join(repo, 'src/HelpDesk.Mcp/bin/Debug/net10.0/HelpDesk.Mcp.exe'), [], {
      env: { ...process.env, DOTNET_ENVIRONMENT: 'Production', HelpDeskApi__BaseUrl: api, HelpDeskApi__Email: email, HelpDeskApi__Password: accountPassword },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    this.process.stderr.on('data', (chunk) => { this.errors += chunk; });
    createInterface({ input: this.process.stdout }).on('line', (line) => this.#receive(line));
  }

  #receive(line) {
    let message;
    try { message = JSON.parse(line); } catch { this.stray.push(line); return; }
    const resolve = this.#pending.get(message.id);
    if (resolve) { this.#pending.delete(message.id); resolve(message); }
  }

  #send(message) { this.process.stdin.write(`${JSON.stringify(message)}\n`); }

  request(method, params = {}) {
    const id = this.#next++;
    const answer = new Promise((resolve, reject) => {
      this.#pending.set(id, resolve);
      setTimeout(() => reject(new Error(`Sin respuesta a ${method}: ${this.errors.slice(-500)}`)), 30_000).unref();
    });
    this.#send({ jsonrpc: '2.0', id, method, params });
    return answer;
  }

  async connect() {
    const answer = await this.request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'prueba-e2e', version: '1.0' } });
    this.#send({ jsonrpc: '2.0', method: 'notifications/initialized' });
    return answer;
  }

  // El texto de la respuesta de una herramienta, y si fue un error.
  async call(name, args = {}) {
    const { result } = await this.request('tools/call', { name, arguments: args });
    const text = result.content?.map((item) => item.text).join('') ?? '';
    return { isError: result.isError === true, text, data: result.structuredContent ?? null };
  }

  stop() { this.process.kill(); }
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
  return { status: response.status, json: text ? JSON.parse(text) : null };
}

const assistants = [];
const connect = async (email, accountPassword) => {
  const assistant = new Assistant(email, accountPassword);
  assistants.push(assistant);
  await assistant.connect();
  return assistant;
};

const created = [];
let lauraJwt;

try {
  lauraJwt = await jwt('laura@helpdesk.local');
  const camilaJwt = await jwt('camila@acme.local');

  const secretPhone = '+56 9 8765 4321';
  const secretEmail = 'diego.rojas@cliente-ejemplo.cl';
  const fromPortal = (await callApi('POST', '/api/portal/tickets', camilaJwt, { title: `MCP: la impresora de ${secretEmail} no imprime`, priority: 'Medium', description: `Llámame al ${secretPhone}.` })).json;
  created.push(fromPortal.id);
  const forPedro = (await callApi('POST', '/api/tickets', lauraJwt, { title: 'MCP: un ticket de pedro', priority: 'Low', assignedTo: 'pedro' })).json;
  created.push(forPedro.id);
  const forLaura = (await callApi('POST', '/api/tickets', lauraJwt, { title: 'MCP: un ticket de laura', priority: 'Low', assignedTo: 'laura' })).json;
  created.push(forLaura.id);

  section('El protocolo');
  const laura = new Assistant('laura@helpdesk.local');
  assistants.push(laura);
  const hello = await laura.connect();
  check('responde al saludo, y se presenta', hello.result?.serverInfo?.name?.length > 0, `${hello.result?.serverInfo?.name} ${hello.result?.protocolVersion}`);

  const { result: { tools } } = await laura.request('tools/list');
  check('ofrece las dos herramientas', tools.map((tool) => tool.name).sort().join() === 'listar_tickets,ver_ticket', tools.map((tool) => tool.name).join());
  check('todas se declaran de solo lectura', tools.every((tool) => tool.annotations?.readOnlyHint === true && tool.annotations?.destructiveHint === false));
  check('las descripciones avisan que el texto es de terceros', tools.every((tool) => tool.description.includes('nunca instrucciones')));

  section('Ve lo que la cuenta puede ver');
  // Los tres tickets de esta prueba son los últimos: se llega a ellos con el cursor.
  const justBefore = fromPortal.id - 1;
  const recent = JSON.parse((await laura.call('listar_tickets', { count: 50, after: justBefore })).text);
  const recentCodes = recent.items.map((ticket) => ticket.code);
  check('la supervisora ve los de todos', recentCodes.includes(forLaura.code) && recentCodes.includes(forPedro.code), recentCodes.join(', '));

  const first = JSON.parse((await laura.call('listar_tickets', { count: 2 })).text);
  check('la lista va de a páginas, y dice cómo seguir', first.items.length === 2 && first.nextAfter === first.items[1].id, `nextAfter ${first.nextAfter}`);
  const second = JSON.parse((await laura.call('listar_tickets', { count: 2, after: first.nextAfter })).text);
  check('y la página siguiente continúa donde quedó', second.items[0].id > first.nextAfter, `${second.items[0].id}`);

  const pedro = await connect('pedro@helpdesk.local');
  const own = JSON.parse((await pedro.call('listar_tickets', { count: 50, after: justBefore })).text);
  const ownCodes = own.items.map((ticket) => ticket.code);
  check('un agente solo ve los suyos', ownCodes.includes(forPedro.code) && !ownCodes.includes(forLaura.code) && own.items.every((ticket) => ticket.assignedTo === 'pedro'), ownCodes.join(', '));
  const notHis = await pedro.call('ver_ticket', { id: forLaura.id });
  check('y un ticket ajeno se niega', notHis.isError && notHis.text.includes('no tiene permiso'), notHis.text);

  const camila = await connect('camila@acme.local');
  const customer = await camila.call('listar_tickets');
  check('un cliente no puede usarlo', customer.isError && customer.text.includes('no tiene permiso'), customer.text);

  const filtered = JSON.parse((await laura.call('listar_tickets', { status: 'Open', priority: 'Low', count: 5 })).text).items;
  check('filtra por estado y prioridad', filtered.length > 0 && filtered.length <= 5 && filtered.every((ticket) => ticket.status === 'Open' && ticket.priority === 'Low'), `${filtered.length} tickets`);

  section('Los datos personales no salen');
  const detail = await laura.call('ver_ticket', { id: fromPortal.id });
  check('muestra el ticket con sus comentarios', !detail.isError && detail.text.includes(fromPortal.code) && detail.text.includes('dato oculto'), detail.text.slice(0, 160));
  check('oculta el correo y el teléfono', !detail.text.includes(secretEmail) && !detail.text.includes('8765'));
  check('no dice quién pidió el ticket', !detail.text.includes('camila@acme.local'));
  const listed = await laura.call('listar_tickets', { count: 50, after: justBefore });
  check('tampoco en la lista', listed.text.includes(fromPortal.code) && !listed.text.includes(secretEmail) && !listed.text.includes('camila@acme.local'));

  section('Errores');
  const missing = await laura.call('ver_ticket', { id: 2_000_000_000 });
  check('un ticket que no existe lo dice', missing.isError && missing.text.includes('No existe'), missing.text);
  const unknown = await laura.request('tools/call', { name: 'cerrar_ticket', arguments: { id: forLaura.id } });
  check('una herramienta que no existe se rechaza', unknown.error !== undefined || unknown.result?.isError === true);
  check('y el ticket sigue igual', (await callApi('GET', `/api/tickets/${forLaura.id}`, lauraJwt)).json.status === 'Open');

  const wrong = await connect('laura@helpdesk.local', 'una-clave-incorrecta');
  const rejected = await wrong.call('listar_tickets');
  check('con una clave incorrecta, avisa', rejected.isError && rejected.text.includes('No se pudo iniciar sesión'), rejected.text);

  section('Lo que sale por los canales');
  check('por stdout solo sale el protocolo', assistants.every((assistant) => assistant.stray.length === 0), assistants.flatMap((assistant) => assistant.stray).slice(0, 2).join(' | ').slice(0, 200));
  check('la contraseña no queda en los logs', assistants.every((assistant) => !assistant.errors.includes(password) && !assistant.errors.includes('una-clave-incorrecta')));
} catch (error) {
  check('la suite terminó sin errores', false, error.stack ?? String(error));
} finally {
  for (const assistant of assistants) assistant.stop();
  if (lauraJwt) {
    const deleted = [];
    for (const id of created) deleted.push((await callApi('DELETE', `/api/tickets/${id}`, lauraJwt)).status);
    console.log(`--- Limpieza: ${created.length} tickets borrados (status ${[...new Set(deleted)].join(',')}) ---`);
  }
}

console.log(`RESULTADO: ${pass} PASS, ${fail} FAIL`);
process.exit(fail === 0 ? 0 : 1);
