import type { SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router';
import { isValidationError, validationErrorsFor } from '../api/client';
import { ErrorMessage } from '../components/ErrorMessage';
import { FormField } from '../components/FormField';
import { useOpenTicket } from '../tickets/hooks';
import { portalPriorities, priorityLabels, type PortalPriority } from '../tickets/labels';

const defaultPriority: PortalPriority = 'Medium';

// Las reglas (largo del título, de la descripción, prioridades permitidas) viven en la API: este formulario no las
// repite. Manda lo que el cliente escribió y muestra los mensajes del 400 junto a cada campo.
export function NewTicketPage() {
  const navigate = useNavigate();
  const openTicket = useOpenTicket();
  const errorsFor = (field: string) => validationErrorsFor(openTicket.error, field);

  function handleSubmit(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);

    openTicket.mutate(
      { body: { title: readText(form, 'title'), description: readText(form, 'description'), priority: readPriority(form) } },
      // Con el ticket ya en la caché (ver useOpenTicket), el detalle se muestra sin esperar otra llamada.
      { onSuccess: (ticket) => navigate(`/tickets/${ticket.id}`) },
    );
  }

  return (
    <section>
      <title>Nuevo ticket · HelpDesk</title>
      <p><Link to="/">← Mis tickets</Link></p>
      <h1>Nuevo ticket</h1>

      {/* Campos no controlados: el navegador guarda lo que se escribe y FormData lo lee al enviar. */}
      <form className="form" onSubmit={handleSubmit}>
        <FormField id="title" label="Título" errors={errorsFor('title')}>
          {(control) => <input {...control} name="title" autoComplete="off" />}
        </FormField>

        <FormField id="description" label="Describe el problema" errors={errorsFor('description')}>
          {(control) => <textarea {...control} name="description" rows={6} />}
        </FormField>

        <FormField id="priority" label="Prioridad" errors={errorsFor('priority')}>
          {(control) => (
            <select {...control} name="priority" defaultValue={defaultPriority}>
              {portalPriorities.map((priority) => (
                <option key={priority} value={priority}>{priorityLabels[priority]}</option>
              ))}
            </select>
          )}
        </FormField>

        {isValidationError(openTicket.error) && (
          <p className="form-summary" role="alert">Revisa los campos marcados.</p>
        )}
        {openTicket.isError && !isValidationError(openTicket.error) && <ErrorMessage error={openTicket.error} />}

        <div className="form-actions">
          {/* Deshabilitado mientras viaja: un doble clic no abre dos tickets. */}
          <button type="submit" className="button" disabled={openTicket.isPending}>
            {openTicket.isPending ? 'Enviando…' : 'Abrir ticket'}
          </button>
          <Link to="/">Cancelar</Link>
        </div>
      </form>
    </section>
  );
}

function readText(form: FormData, name: string): string {
  const value = form.get(name);
  return typeof value === 'string' ? value : '';
}

// Busca el valor en la lista permitida en vez de forzar el tipo con "as": si llegara otra cosa, usa la de siempre.
function readPriority(form: FormData): PortalPriority {
  const value = form.get('priority');
  return portalPriorities.find((priority) => priority === value) ?? defaultPriority;
}
