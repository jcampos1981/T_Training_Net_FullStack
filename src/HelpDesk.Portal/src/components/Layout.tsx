import type { ReactNode } from 'react';
import { Link, Outlet } from 'react-router';
import { $api } from '../api/client';
import { loginUrl, useSession } from '../auth/session';
import { ErrorMessage } from './ErrorMessage';

// Marco común de todas las pantallas: encabezado con el usuario, y el contenido según el estado de la sesión.
export function Layout() {
  const session = useSession();

  return (
    <div className="app">
      <header className="app-header">
        <Link to="/" className="brand">HelpDesk · Portal del cliente</Link>
        {session.data && (
          <nav className="user">
            <span>{session.data.email ?? session.data.name}</span>
            {/* Navegación normal: el BFF cierra su sesión y la de Keycloak, y vuelve a "/". */}
            <a href={session.data.logoutUrl}>Cerrar sesión</a>
          </nav>
        )}
      </header>

      <main className="app-main">
        {session.isPending && <p>Cargando…</p>}
        {session.isError && <ErrorMessage error={session.error} />}
        {session.data === null && <Welcome />}
        {session.data && (
          <CustomerOnly>
            <Outlet />
          </CustomerOnly>
        )}
      </main>
    </div>
  );
}

function Welcome() {
  return (
    <section className="welcome">
      <h1>Soporte para clientes</h1>
      <p>Abre tickets y sigue la respuesta del equipo de soporte.</p>
      <a className="button" href={loginUrl()}>Iniciar sesión</a>
      <p className="hint">¿No tienes cuenta? En la página de inicio de sesión puedes registrarte.</p>
    </section>
  );
}

// El portal es para clientes. Una cuenta del equipo (pedro, laura) inicia sesión igual, pero la API le respondería
// 403 en cada pantalla: mejor decirlo una vez, con /api/me, que es la fuente de verdad de los roles.
function CustomerOnly({ children }: { children: ReactNode }) {
  const me = $api.useQuery('get', '/api/me');

  if (me.isPending) {
    return <p>Cargando…</p>;
  }

  if (me.isError) {
    return <ErrorMessage error={me.error} />;
  }

  if (!me.data.roles.includes('Customer')) {
    return (
      <div className="alert" role="alert">
        <strong>Esta cuenta no es de cliente</strong>
        <p>{me.data.userName} es del equipo de soporte; el portal es para clientes. Usa el panel del equipo.</p>
      </div>
    );
  }

  return children;
}
