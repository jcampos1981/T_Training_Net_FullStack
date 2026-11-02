import type { ReactNode } from 'react';

// Lo que el control (input, textarea, select) necesita para quedar conectado con su etiqueta y sus errores.
interface FieldControlProps {
  id: string;
  'aria-invalid': boolean;
  'aria-describedby': string | undefined;
}

interface FormFieldProps {
  id: string;
  label: string;
  errors: string[];
  // Render prop: FormField arma los atributos y quien lo usa decide qué control dibujar con ellos.
  children: (control: FieldControlProps) => ReactNode;
}

// Etiqueta + control + mensajes de validación de la API. aria-describedby enlaza el campo con sus errores: un lector
// de pantalla los lee al llegar al campo, no solo quien los ve en rojo.
export function FormField({ id, label, errors, children }: FormFieldProps) {
  const errorsId = `${id}-errors`;
  const hasErrors = errors.length > 0;

  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children({ id, 'aria-invalid': hasErrors, 'aria-describedby': hasErrors ? errorsId : undefined })}
      {hasErrors && (
        <ul id={errorsId} className="field-errors">
          {errors.map((message) => (
            <li key={message}>{message}</li>
          ))}
        </ul>
      )}
    </div>
  );
}
