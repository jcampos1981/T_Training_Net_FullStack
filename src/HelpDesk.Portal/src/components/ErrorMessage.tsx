import { ApiError } from '../api/client';

// openapi-react-query tipa el error con el cuerpo que declara el OpenAPI (ProblemDetails), pero en tiempo de
// ejecución llega el ApiError que lanza el middleware de client.ts. Por eso se recibe "unknown" y se revisa aquí,
// en vez de forzar el tipo con un "as".
export function ErrorMessage({ error }: { error: unknown }) {
  const { title, detail } = describe(error);

  return (
    <div className="alert" role="alert">
      <strong>{title}</strong>
      <p>{detail}</p>
    </div>
  );
}

function describe(error: unknown): { title: string; detail: string } {
  if (error instanceof ApiError) {
    return { title: error.problem?.title ?? `Error ${error.status}`, detail: error.message };
  }

  if (error instanceof Error) {
    return { title: 'No se pudo conectar', detail: error.message };
  }

  return { title: 'Error inesperado', detail: String(error) };
}
