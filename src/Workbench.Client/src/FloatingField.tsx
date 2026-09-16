import type { ReactNode } from 'react';

/** Wrap one native control with its accessible label. Inputs and textareas need a
 * nonempty placeholder so CSS recognizes empty values. Select labels stay raised.
 * Use compact only for short labels that fit the smaller responsive threshold. */
export function FloatingField({ label, htmlFor, children, compact = false }: {
  label: string;
  htmlFor: string;
  children: ReactNode;
  compact?: boolean;
}) {
  return (
    <div className={`floating-field${compact ? ' floating-field-compact' : ''}`}>
      {children}
      <label className="field-label" htmlFor={htmlFor}>{label}</label>
    </div>
  );
}
