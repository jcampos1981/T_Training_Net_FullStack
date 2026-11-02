import { Link } from 'react-router';
import { ErrorMessage } from '../components/ErrorMessage';
import { useMyTickets } from '../tickets/hooks';
import { TicketTable } from '../tickets/TicketTable';

export function MyTicketsPage() {
  const tickets = useMyTickets();

  if (tickets.isPending) {
    return <p>Cargando tus tickets…</p>;
  }

  // Error al cargar la PRIMERA página: no hay nada que mostrar. Si falla "Cargar más", las páginas que ya
  // llegaron siguen en pantalla y el error se muestra abajo (isRefetchError).
  if (tickets.isLoadingError) {
    return <ErrorMessage error={tickets.error} />;
  }

  const items = tickets.data.pages.flatMap((page) => page.items);

  return (
    <section>
      {/* React 19: un <title> dentro de un componente se mueve solo al <head> del documento. */}
      <title>Mis tickets · HelpDesk</title>

      <div className="page-title">
        <h1>Mis tickets</h1>
        <Link className="button" to="/tickets/new">Nuevo ticket</Link>
      </div>

      {items.length === 0 ? <p>Todavía no tienes tickets.</p> : <TicketTable tickets={items} />}

      {tickets.isRefetchError && <ErrorMessage error={tickets.error} />}

      {tickets.hasNextPage && (
        <p className="load-more">
          <button
            type="button"
            className="button secondary"
            disabled={tickets.isFetchingNextPage}
            onClick={() => tickets.fetchNextPage()}
          >
            {tickets.isFetchingNextPage ? 'Cargando…' : 'Cargar más'}
          </button>
        </p>
      )}
    </section>
  );
}
