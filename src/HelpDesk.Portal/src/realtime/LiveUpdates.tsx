import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { sessionQueryKey } from '../auth/session';
import { refreshTicketData } from '../tickets/hooks';
import { statusLabels } from '../tickets/labels';
import { connectTicketEvents, type PortalTicketEvent } from './ticketEvents';

const maxNoticesShown = 3;
const noticeDurationMs = 10_000;

interface Notice {
  id: number;
  event: PortalTicketEvent;
}

let nextNoticeId = 0;

function noticeText({ kind, code, status }: PortalTicketEvent): string {
  switch (kind) {
    case 'Commented':
      return `Soporte respondió en ${code}.`;
    case 'Updated':
      return `Soporte actualizó ${code}. Estado: ${statusLabels[status]}.`;
    case 'Created':
      return `Soporte creó el ticket ${code}.`;
    case 'Deleted':
      return `Soporte eliminó el ticket ${code}.`;
  }
}

// Sin dibujar nada de la conexión mientras funciona. Un aviso de la API no trae los datos: solo dice qué cambió, y
// TanStack Query vuelve a pedir lo que esté en pantalla, con los mismos permisos de siempre.
export function LiveUpdates() {
  const queryClient = useQueryClient();
  const [notices, setNotices] = useState<Notice[]>([]);
  const [isLive, setIsLive] = useState(true);

  const dismiss = useCallback((id: number) => setNotices((current) => current.filter((notice) => notice.id !== id)), []);

  useEffect(
    () =>
      connectTicketEvents({
        onTicketChanged: (event) => {
          void refreshTicketData(queryClient, event.ticketId);

          // Lo que hizo el mismo cliente (en otra pestaña) actualiza la pantalla, pero no merece un aviso.
          if (event.bySupport) {
            setNotices((current) => [...current.slice(1 - maxNoticesShown), { id: nextNoticeId++, event }]);
          }
        },
        onReconnected: () => void queryClient.invalidateQueries(),
        onConnectionChange: setIsLive,
        onSessionEnded: () => queryClient.setQueryData(sessionQueryKey, null),
      }),
    [queryClient],
  );

  return (
    <>
      {/* role="status": los lectores de pantalla anuncian lo que aparece aquí, sin interrumpir lo que estén leyendo. */}
      <div role="status">
        {!isLive && <p className="live-offline">Sin conexión en vivo. Reintentando; tus tickets podrían no estar al día.</p>}
      </div>
      <div className="notices" role="status">
        {notices.map((notice) => (
          <NoticeItem key={notice.id} notice={notice} onDismiss={dismiss} />
        ))}
      </div>
    </>
  );
}

function NoticeItem({ notice: { id, event }, onDismiss }: { notice: Notice; onDismiss: (id: number) => void }) {
  useEffect(() => {
    const timer = setTimeout(() => onDismiss(id), noticeDurationMs);
    return () => clearTimeout(timer);
  }, [id, onDismiss]);

  return (
    <div className="notice">
      <span>{noticeText(event)}</span>
      {event.kind !== 'Deleted' && <Link to={`/tickets/${event.ticketId}`} onClick={() => onDismiss(id)}>Ver</Link>}
      <button type="button" className="notice-close" aria-label="Cerrar aviso" onClick={() => onDismiss(id)}>×</button>
    </div>
  );
}
