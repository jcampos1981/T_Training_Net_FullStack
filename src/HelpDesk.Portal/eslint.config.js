import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';
import { defineConfig, globalIgnores } from 'eslint/config';

export default defineConfig([
  // schema.ts lo genera openapi-typescript: no se edita a mano, así que tampoco se revisa.
  globalIgnores(['dist', 'src/api/schema.ts']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended, // reglas de los hooks (dependencias de useEffect, orden de llamada...)
      reactRefresh.configs.vite,           // avisa si un archivo rompe la recarga en caliente
    ],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
  },
]);
