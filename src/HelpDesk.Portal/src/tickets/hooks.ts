import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { $api, hasStatus } from '../api/client';
import type { components } from '../api/schema';

// Las consultas y envíos del portal, en un solo lugar. Las pantallas piden "mis tickets" o "responder"; las llaves de
// la caché y qué invalidar después de cada cambio se deciden aquí.

type Comment = components['schemas']['CommentResponse'];

const pageSize = 20;

// openapi-react-query arma cada llave como [método, ruta, parámetros]. Invalidar con el prefijo [método, ruta]
// alcanza a todas sus variantes (cualquier tamaño de página).
const myTicketsKey = ['get', '/api/portal/tickets'] as const;

// La API devuelve la conversación del mensaje más reciente al más antiguo; en pantalla se lee de arriba hacia abajo.
// "select" transforma lo que recibe el componente sin tocar lo que queda en la caché.
const oldestFirst = (comments: Comment[]): Comment[] => [...comments].reverse();

// Cada consulta se define una vez (llave + función) y se usa tanto para leer como para invalidar: así la llave
// no puede quedar distinta entre los dos lugares.
const ticketQuery = (id: number) =>
  $api.queryOptions('get', '/api/portal/tickets/{id}', { params: { path: { id } } });

const conversationQuery = (ticketId: number) =>
  $api.queryOptions('get', '/api/portal/tickets/{id}/comments', { params: { path: { id: ticketId } } }, { select: oldestFirst });

// Paginación por keyset: la primera página va con After=0 y cada "Cargar más" pide After=<nextAfter> de la última.
// TanStack Query guarda las páginas juntas (data.pages) bajo una sola llave. "After" va con mayúscula porque así
// lo publica el OpenAPI; la API no distingue mayúsculas en el query string.
export function useMyTickets() {
  return $api.useInfiniteQuery(
    'get',
    '/api/portal/tickets',
    { params: { query: { PageSize: pageSize } } },
    {
      pageParamName: 'After',
      initialPageParam: 0,
      getNextPageParam: (lastPage) => lastPage.nextAfter ?? undefined, // undefined = no hay más páginas
    },
  );
}

export function useTicket(id: number) {
  return useQuery(ticketQuery(id));
}

export function useConversation(ticketId: number) {
  return useQuery(conversationQuery(ticketId));
}

// Un aviso en vivo dijo que este ticket cambió. Solo se vuelve a pedir lo que esté en pantalla; lo demás queda marcado
// como desactualizado y se pedirá cuando se muestre.
export async function refreshTicketData(queryClient: QueryClient, ticketId: number): Promise<void> {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: myTicketsKey }),
    queryClient.invalidateQueries({ queryKey: ticketQuery(ticketId).queryKey }),
    queryClient.invalidateQueries({ queryKey: conversationQuery(ticketId).queryKey }),
  ]);
}

export function useOpenTicket() {
  const queryClient = useQueryClient();

  return $api.useMutation('post', '/api/portal/tickets', {
    onSuccess: async (ticket) => {
      // El 201 ya trae el ticket: se guarda en la caché y la pantalla de detalle no tiene que volver a pedirlo.
      queryClient.setQueryData(ticketQuery(ticket.id).queryKey, ticket);
      // La lista quedó desactualizada: se marca y se vuelve a pedir la próxima vez que se muestre.
      await queryClient.invalidateQueries({ queryKey: myTicketsKey });
    },
  });
}

export function useReply(ticketId: number) {
  const queryClient = useQueryClient();

  return $api.useMutation('post', '/api/portal/tickets/{id}/comments', {
    onSuccess: () => queryClient.invalidateQueries({ queryKey: conversationQuery(ticketId).queryKey }),
    onError: async (error) => {
      // 409: soporte cerró el ticket mientras el cliente escribía. Se recarga para que la pantalla lo muestre cerrado.
      if (hasStatus(error, 409)) {
        await queryClient.invalidateQueries({ queryKey: ticketQuery(ticketId).queryKey });
      }
    },
  });
}
