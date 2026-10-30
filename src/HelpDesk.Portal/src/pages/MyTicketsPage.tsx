import { $api } from '../api/client';
import { ErrorMessage } from '../components/ErrorMessage';
import { formatDate, priorityLabels, statusLabels } from '../tickets/labels';

const pageSize = 20;

export function MyTicketsPage() {
  // La "query key" la arma openapi-react-query con el método, la ruta y los parámetros: otra página u otro
  // pageSize son otra entrada en la caché.
  const tickets = $api.useQuery('get', '/api/portal/tickets', {
    params: { query: { PageSize: pageSize } },
  });

  if (tickets.isPending) {
    return <p>Cargando tus tickets…</p>;
  }

  if (tickets.isError) {
    return <ErrorMessage error={tickets.error} />;
  }

  const { items } = tickets.data;

  return (
    <section>
      <h1>Mis tickets</h1>

      {items.length === 0 ? (
        <p>Todavía no tienes tickets.</p>
      ) : (
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
            {items.map((ticket) => (
              <tr key={ticket.id}>
                <td className="code">{ticket.code}</td>
                <td>{ticket.title}</td>
                <td>{priorityLabels[ticket.priority]}</td>
                <td><span className={`status status-${ticket.status}`}>{statusLabels[ticket.status]}</span></td>
                <td>{formatDate(ticket.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
