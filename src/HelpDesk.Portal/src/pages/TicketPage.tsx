import { Link, useParams } from 'react-router';
import { hasStatus } from '../api/client';
import { ErrorMessage } from '../components/ErrorMessage';
import { Conversation } from '../tickets/Conversation';
import { useConversation, useTicket } from '../tickets/hooks';
import { formatDate, priorityLabels } from '../tickets/labels';
import { ReplyForm } from '../tickets/ReplyForm';
import { StatusBadge } from '../tickets/StatusBadge';
import { NotFoundPage } from './NotFoundPage';

// /tickets/:id. Un id que no es un número positivo ni llega a la API.
export function TicketPage() {
  const ticketId = parseTicketId(useParams().id);

  // key: al pasar de un ticket a otro, React arma el detalle de cero (el borrador de respuesta no se arrastra).
  return ticketId === null ? <NotFoundPage /> : <TicketDetail key={ticketId} ticketId={ticketId} />;
}

function TicketDetail({ ticketId }: { ticketId: number }) {
  // Las dos consultas salen en paralelo: la conversación no espera a que llegue el ticket.
  const ticket = useTicket(ticketId);
  const conversation = useConversation(ticketId);

  if (ticket.isPending) {
    return <p>Cargando el ticket…</p>;
  }

  if (ticket.isError) {
    // La API responde 404 tanto si no existe como si es de otro cliente: aquí tampoco se distinguen.
    return hasStatus(ticket.error, 404) ? <TicketNotFound /> : <ErrorMessage error={ticket.error} />;
  }

  const { code, title, status, priority, createdAt } = ticket.data;

  return (
    <section>
      {/* Un solo texto: <title>{code} · HelpDesk</title> serían dos hijos, y <title> admite uno. */}
      <title>{`${code} · HelpDesk`}</title>
      <p><Link to="/">← Mis tickets</Link></p>
      <h1><span className="code">{code}</span> {title}</h1>

      <dl className="ticket-facts">
        <dt>Estado</dt>
        <dd><StatusBadge status={status} /></dd>
        <dt>Prioridad</dt>
        <dd>{priorityLabels[priority]}</dd>
        <dt>Creado</dt>
        <dd>{formatDate(createdAt)}</dd>
      </dl>

      <h2>Conversación</h2>
      {conversation.isPending && <p>Cargando la conversación…</p>}
      {conversation.isError && <ErrorMessage error={conversation.error} />}
      {conversation.isSuccess && <Conversation comments={conversation.data} />}

      {status === 'Closed'
        ? <p className="hint">Este ticket está cerrado. Si el problema volvió, abre un ticket nuevo.</p>
        : <ReplyForm ticketId={ticketId} />}
    </section>
  );
}

function TicketNotFound() {
  return (
    <section>
      <title>Ticket no encontrado · HelpDesk</title>
      <h1>No encontramos ese ticket</h1>
      <p>Puede que el enlace esté mal o que el ticket no sea de tu cuenta.</p>
      <p><Link to="/">Volver a mis tickets</Link></p>
    </section>
  );
}

function parseTicketId(raw: string | undefined): number | null {
  return raw !== undefined && /^[1-9]\d{0,8}$/.test(raw) ? Number(raw) : null;
}
