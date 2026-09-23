import type { ReactNode } from 'react';
import { fieldId } from './accountingValidation';

export type FieldErrors = Record<string, string[]>;
export function FieldError({ field, errors }: { field: string; errors: FieldErrors }) {
  return errors[field]?.length ? <p id={`${fieldId(field)}-error`} className="accounting-field-error">{errors[field].join(' ')}</p> : null;
}

interface ControlAttributes {
  id: string;
  'aria-invalid': true | undefined;
  'aria-describedby': string | undefined;
}

export function AccountingField({ field, label, errors = {}, help, children }: {
  field: string;
  label: string;
  errors?: FieldErrors;
  help?: string;
  children(attributes: ControlAttributes): ReactNode;
}) {
  const id = fieldId(field);
  const invalid = !!errors[field]?.length;
  const description = [help ? `${id}-help` : '', invalid ? `${id}-error` : ''].filter(Boolean).join(' ') || undefined;
  return <div className="accounting-field">
    <label htmlFor={id}>{label}</label>
    {children({ id, 'aria-invalid': invalid ? true : undefined, 'aria-describedby': description })}
    {help ? <p id={`${id}-help`} className="accounting-help">{help}</p> : null}
    <FieldError field={field} errors={errors} />
  </div>;
}
