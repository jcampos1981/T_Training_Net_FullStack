import { Link } from 'react-router';
import type { components } from '../api/schema';
import { formatDate, priorityLabels } from './labels';
import { StatusBadge } from './StatusBadge';

type PortalTicket = components['schemas']['PortalTicketResponse'];

export function TicketTable({ tickets }: { tickets: PortalTicket[] }) {
  return (
    <table className="tickets">
      <thead>
        <tr>
          <th>Código</th>
          <th>Título</th>
          <th>Prioridad</th>
          <th>Estado</th>
          <th>Creado</th>
        </tr>
      </thead>
      <tbody>
        {tickets.map((ticket) => (
          <tr key={ticket.id}>
            <td className="code">{ticket.code}</td>
            <td><Link to={`/tickets/${ticket.id}`}>{ticket.title}</Link></td>
            <td>{priorityLabels[ticket.priority]}</td>
            <td><StatusBadge status={ticket.status} /></td>
            <td>{formatDate(ticket.createdAt)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
