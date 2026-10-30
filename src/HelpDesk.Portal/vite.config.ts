import { defineConfig, type ProxyOptions } from 'vite';
import react from '@vitejs/plugin-react';

// En desarrollo, el navegador solo habla con Vite (http://localhost:5173). Vite sirve la app con recarga en
// caliente y reenvía al BFF todo lo que es del BFF: para el navegador hay un solo origen, como en producción,
// así que no hace falta CORS y la cookie de sesión funciona igual.
const bff: ProxyOptions = {
  target: 'https://localhost:7200',
  // El certificado de desarrollo de ASP.NET Core no es de confianza para Node (usa su propia lista de CAs).
  secure: false,
  // X-Forwarded-Proto: http y X-Forwarded-For: el BFF arma la redirect_uri de Keycloak con el esquema y el host
  // que ve el navegador (http://localhost:5173), no con los de la conexión Vite → BFF (https://localhost:7200).
  xfwd: true,
};

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true, // si el puerto está ocupado, falla en vez de usar otro (las redirect_uri dependen de él)
    proxy: {
      '/api': bff,                      // la API, a través del BFF (que agrega el token)
      '/bff': bff,                      // login, usuario actual y logout
      '/signin-oidc': bff,              // Keycloak vuelve aquí después del login
      '/signout-callback-oidc': bff,    // y aquí después del logout
    },
  },
});
