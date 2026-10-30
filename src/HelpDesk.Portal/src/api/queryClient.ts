import { QueryCache, QueryClient } from '@tanstack/react-query';
import { sessionQueryKey } from '../auth/session';
import { ApiError } from './client';

// Reintentar sirve para fallas pasajeras (red, 5xx). Un 4xx no mejora reintentando: 404 seguirá siendo 404.
function shouldRetry(failureCount: number, error: Error): boolean {
  const isClientError = error instanceof ApiError && error.status < 500;
  return !isClientError && failureCount < 2;
}

export const queryClient: QueryClient = new QueryClient({
  queryCache: new QueryCache({
    // Un 401 en cualquier consulta significa que la sesión del BFF terminó (venció, o se cerró en Keycloak):
    // se marca la sesión como vacía y la app muestra "Iniciar sesión" en vez de un error.
    onError: (error) => {
      if (error instanceof ApiError && error.status === 401) {
        queryClient.setQueryData(sessionQueryKey, null);
      }
    },
  }),
  defaultOptions: {
    queries: {
      // Durante 30 s los datos se consideran frescos: volver a una pantalla no repite la llamada.
      staleTime: 30_000,
      retry: shouldRetry,
    },
  },
});
