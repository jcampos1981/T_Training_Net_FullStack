import { useState, type SubmitEvent } from 'react';
import { isValidationError, validationErrorsFor } from '../api/client';
import { ErrorMessage } from '../components/ErrorMessage';
import { FormField } from '../components/FormField';
import { useReply } from './hooks';

export function ReplyForm({ ticketId }: { ticketId: number }) {
  const reply = useReply(ticketId);
  // Campo controlado (a diferencia de "Nuevo ticket"): hay que vaciarlo después de enviar, y conservarlo si falla.
  const [body, setBody] = useState('');

  function handleSubmit(event: SubmitEvent<HTMLFormElement>) {
    event.preventDefault();
    reply.mutate({ params: { path: { id: ticketId } }, body: { body } }, { onSuccess: () => setBody('') });
  }

  return (
    <form className="form" onSubmit={handleSubmit}>
      <FormField id="reply" label="Tu respuesta" errors={validationErrorsFor(reply.error, 'body')}>
        {(control) => (
          <textarea {...control} name="body" rows={4} value={body} onChange={(event) => setBody(event.target.value)} />
        )}
      </FormField>

      {/* 409 si el ticket se cerró mientras escribías: la API lo explica en "detail". */}
      {reply.isError && !isValidationError(reply.error) && <ErrorMessage error={reply.error} />}

      <div className="form-actions">
        <button type="submit" className="button" disabled={reply.isPending}>
          {reply.isPending ? 'Enviando…' : 'Responder'}
        </button>
      </div>
    </form>
  );
}
