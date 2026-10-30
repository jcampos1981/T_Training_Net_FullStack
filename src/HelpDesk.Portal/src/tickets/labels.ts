import type { components } from '../api/schema';

export type TicketStatus = components['schemas']['TicketStatus'];
export type TicketPriority = components['schemas']['TicketPriority'];

// Record<Unión, string> obliga a tener una etiqueta para CADA valor: si la API agrega un estado nuevo y se
// regeneran los tipos, esto deja de compilar hasta que alguien escriba su etiqueta.
export const statusLabels: Record<TicketStatus, string> = {
  Open: 'Abierto',
  InProgress: 'En atención',
  Resolved: 'Resuelto',
  Closed: 'Cerrado',
};

export const priorityLabels: Record<TicketPriority, string> = {
  Low: 'Baja',
  Medium: 'Media',
  High: 'Alta',
  Critical: 'Crítica',
};

const dateFormat = new Intl.DateTimeFormat('es', { dateStyle: 'medium', timeStyle: 'short' });

export function formatDate(isoDate: string): string {
  return dateFormat.format(new Date(isoDate));
}
