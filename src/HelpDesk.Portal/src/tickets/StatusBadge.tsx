import { statusLabels, type TicketStatus } from './labels';

export function StatusBadge({ status }: { status: TicketStatus }) {
  return <span className={`status status-${status}`}>{statusLabels[status]}</span>;
}
