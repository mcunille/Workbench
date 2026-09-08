import type { ReactNode } from 'react';

/** Wrap one input or textarea with its accessible label. Use a nonempty placeholder
 * (a single space when no hint is needed) so CSS can recognize empty values. */
export function FloatingField({ label, htmlFor, children }: {
  label: string;
  htmlFor: string;
  children: ReactNode;
}) {
  return (
    <div className="floating-field">
      {children}
      <label className="field-label" htmlFor={htmlFor}>{label}</label>
    </div>
  );
}
