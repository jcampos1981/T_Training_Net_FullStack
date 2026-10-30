import { Link } from 'react-router';

export function NotFoundPage() {
  return (
    <section>
      <h1>No encontramos esa página</h1>
      <p><Link to="/">Volver a mis tickets</Link></p>
    </section>
  );
}
