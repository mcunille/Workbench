import type { ReactNode } from 'react';

/** Wrap one input or textarea with its accessible label. Use a nonempty placeholder
 * (a single space when no hint is needed) so CSS can recognize empty values. */
export function FloatingField({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="floating-field">
      {children}
      <span className="field-label">{label}</span>
    </label>
  );
}
