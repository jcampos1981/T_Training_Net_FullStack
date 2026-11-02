import createFetchClient, { type Middleware } from 'openapi-fetch';
import createQueryClient from 'openapi-react-query';
import type { paths } from './schema';

// RFC 9457: el formato de error de la API (y del BFF). "errors" viene en los 400 de validación, con las claves en
// camelCase como los campos del JSON ("title", no "Title").
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
}

// Un error HTTP con su status y el ProblemDetails que lo explica (si vino).
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | null;

  constructor(status: number, problem: ProblemDetails | null) {
    super(problem?.detail ?? problem?.title ?? `HTTP ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

// Las funciones que siguen reciben "unknown": openapi-react-query tipa el error con el cuerpo que declara el
// OpenAPI (ProblemDetails), pero en tiempo de ejecución llega el ApiError que lanza el middleware. Se revisa con
// instanceof en vez de forzar el tipo con un "as".

export function hasStatus(error: unknown, status: number): error is ApiError {
  return error instanceof ApiError && error.status === status;
}

// Un 400 con mensajes por campo: la pantalla los muestra junto a cada campo, no como un error general.
export function isValidationError(error: unknown): error is ApiError {
  return hasStatus(error, 400) && error.problem?.errors !== undefined;
}

export function validationErrorsFor(error: unknown, field: string): string[] {
  return isValidationError(error) ? (error.problem?.errors?.[field] ?? []) : [];
}

async function readProblem(response: Response): Promise<ProblemDetails | null> {
  const contentType = response.headers.get('content-type') ?? '';
  return contentType.includes('json') ? ((await response.clone().json()) as ProblemDetails) : null;
}

// Toda respuesta que no sea 2xx se convierte en ApiError. Así TanStack Query la trata como error, y la pantalla
// sabe si fue 401 (sesión vencida), 404, 409 o 400 con errores de validación.
const throwOnError: Middleware = {
  async onResponse({ response }) {
    if (!response.ok) {
      throw new ApiError(response.status, await readProblem(response));
    }
    return undefined;
  },
};

// Cliente tipado con los tipos que openapi-typescript genera desde /openapi/v1.json (npm run api:generate):
// las rutas, los parámetros y las respuestas se revisan al compilar. Si la API cambia un campo, el error aparece
// aquí y no en producción.
export const fetchClient = createFetchClient<paths>({
  // Mismo origen: en desarrollo lo recibe el proxy de Vite y en producción el BFF; los dos lo reenvían a la API.
  baseUrl: window.location.origin,
  // El BFF exige este header en /api: otra página no puede agregarlo sin permiso CORS (protección CSRF).
  headers: { 'X-CSRF': '1' },
});

fetchClient.use(throwOnError);

// Hooks de TanStack Query sobre el mismo cliente: $api.useQuery('get', '/api/portal/tickets', ...).
export const $api = createQueryClient(fetchClient);
