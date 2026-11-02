import { createBrowserRouter } from 'react-router';
import { Layout } from './components/Layout';
import { MyTicketsPage } from './pages/MyTicketsPage';
import { NewTicketPage } from './pages/NewTicketPage';
import { NotFoundPage } from './pages/NotFoundPage';
import { TicketPage } from './pages/TicketPage';

// Rutas del lado del navegador. En producción el BFF devuelve index.html para cualquier ruta que no sea un archivo
// ni /api ni /bff, y React Router decide qué pantalla mostrar. "tickets/new" gana sobre "tickets/:id" porque un
// segmento fijo pesa más que un parámetro, sin importar el orden en que se declaren.
export const router = createBrowserRouter([
  {
    path: '/',
    element: <Layout />,
    children: [
      { index: true, element: <MyTicketsPage /> },
      { path: 'tickets/new', element: <NewTicketPage /> },
      { path: 'tickets/:id', element: <TicketPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
