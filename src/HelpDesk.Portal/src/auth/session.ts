import { useQuery } from '@tanstack/react-query';

// Lo que devuelve GET /bff/user. No está en el OpenAPI de la API: es un endpoint del BFF.
export interface BffUser {
  name: string;
  email: string | null;
  logoutUrl: string;
}

export const sessionQueryKey = ['bff', 'user'] as const;

// La app no ve tokens ni sabe nada de OpenID Connect: le pregunta al BFF si hay sesión.
async function fetchSession(): Promise<BffUser | null> {
  const response = await fetch('/bff/user');

  if (response.status === 401) {
    return null; // sin sesión: no es un error, es un estado
  }

  if (!response.ok) {
    throw new Error(`No se pudo consultar la sesión (HTTP ${response.status}).`);
  }

  return (await response.json()) as BffUser;
}

export function useSession() {
  return useQuery({ queryKey: sessionQueryKey, queryFn: fetchSession, staleTime: 5 * 60_000 });
}

// Iniciar sesión es una navegación (no un fetch): el navegador va al BFF, de ahí a Keycloak, y vuelve a returnUrl.
export function loginUrl(returnUrl: string = window.location.pathname): string {
  return `/bff/login?returnUrl=${encodeURIComponent(returnUrl)}`;
}
