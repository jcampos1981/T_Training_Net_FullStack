import { createBrowserRouter } from 'react-router';
import { Layout } from './components/Layout';
import { MyTicketsPage } from './pages/MyTicketsPage';
import { NotFoundPage } from './pages/NotFoundPage';

// Rutas del lado del navegador. En producción el BFF devuelve index.html para cualquier ruta desconocida, y
// React Router decide qué pantalla mostrar.
export const router = createBrowserRouter([
  {
    path: '/',
    element: <Layout />,
    children: [
      { index: true, element: <MyTicketsPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
