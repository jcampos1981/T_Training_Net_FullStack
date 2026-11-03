import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { fetchSession } from '../auth/session';
import type { TicketStatus } from '../tickets/labels';

// El aviso que manda la API (PortalTicketEvent). El hub no aparece en el OpenAPI, así que este tipo se mantiene a mano:
// si la API cambia el contrato, el compilador no se entera. Solo las pruebas de punta a punta lo detectan.
export interface PortalTicketEvent {
  ticketId: number;
  code: string;
  title: string;
  status: TicketStatus;
  kind: 'Created' | 'Updated' | 'Commented' | 'Deleted';
  bySupport: boolean;
  occurredAt: string;
}

export interface TicketEventsHandlers {
  onTicketChanged: (event: PortalTicketEvent) => void;
  // Volvió la conexión después de un corte. SignalR no guarda lo que se envió mientras tanto: hay que volver a preguntar.
  onReconnected: () => void;
  onConnectionChange: (isLive: boolean) => void;
  onSessionEnded: () => void;
}

const maxRetryDelayMs = 30_000;

// Una conexión que no llegó a durar esto cuenta como un intento fallido: el servidor la aceptó y la cerró enseguida. Pasa
// con Redis caído, porque no puede unirla a sus grupos. Sin esta regla, el cliente reintentaría al instante, en bucle.
const stableConnectionMs = 30_000;

// Espera cada vez más entre intentos, y con azar: si la API se reinicia, los clientes no vuelven todos en el mismo instante.
function retryDelay(attempt: number): number {
  const delay = Math.min(maxRetryDelayMs, 1_000 * 2 ** attempt);
  return delay / 2 + Math.random() * (delay / 2);
}

// Abre la conexión y la mantiene viva hasta que se llama a la función que devuelve.
//
// No hay token aquí: el navegador se conecta al BFF con su cookie, y el BFF agrega el token al reenviar al hub. Sin BFF,
// el token tendría que ir en la URL (?access_token=), porque el navegador no deja poner headers en un WebSocket.
export function connectTicketEvents(handlers: TicketEventsHandlers): () => void {
  const connection = new HubConnectionBuilder()
    // X-CSRF para los transportes que usan fetch (negotiate); el WebSocket lo revisa el BFF por su Origin.
    .withUrl('/hubs/tickets', { headers: { 'X-CSRF': '1' } })
    // Las fallas ya se manejan abajo: un corte de red es esperable, y no debe ensuciar la consola.
    .configureLogging(LogLevel.None)
    .build();

  let stopped = false;
  let failures = 0;
  let needsResync = false;
  let connectedAt = 0;
  let retryTimer: ReturnType<typeof setTimeout> | undefined;

  function retryLater(): void {
    // Solo un intento fallido cambia el estado: el cierre rutinario al vencer el token no debe notarse en pantalla.
    handlers.onConnectionChange(false);
    needsResync = true;
    retryTimer = setTimeout(() => void connect(), retryDelay(failures));
    failures += 1;
  }

  // Un solo camino para las tres situaciones: el primer intento, un corte de red y el cierre al vencer el token.
  // withAutomaticReconnect() no cubre el primer intento, y se rinde después de cuatro intentos.
  async function connect(): Promise<void> {
    try {
      await connection.start();
    } catch {
      // SignalR no dice por qué falló: un 401 en negotiate llega como un error genérico. Se le pregunta al BFF. Si el BFF
      // tampoco responde (undefined), es un corte y hay que seguir intentando; null es que la sesión terminó.
      const session = stopped ? undefined : await fetchSession().catch(() => undefined);

      // stopped: StrictMode monta dos veces en desarrollo, y la primera conexión se detiene mientras negociaba.
      if (stopped) {
        return;
      }

      if (session === null) {
        handlers.onSessionEnded();
        return;
      }

      retryLater();
      return;
    }

    connectedAt = Date.now();
    handlers.onConnectionChange(true);
    if (needsResync) {
      needsResync = false;
      handlers.onReconnected();
    }
  }

  connection.on('MyTicketChanged', handlers.onTicketChanged);
  connection.onclose(() => {
    if (stopped) {
      return;
    }

    if (Date.now() - connectedAt < stableConnectionMs) {
      retryLater();
      return;
    }

    failures = 0;
    needsResync = true;
    void connect();
  });

  void connect();

  return () => {
    stopped = true;
    clearTimeout(retryTimer);
    void connection.stop();
  };
}
