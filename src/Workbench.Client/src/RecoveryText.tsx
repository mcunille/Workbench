import { useId, useRef } from 'react';

/** Pass only the editor's business fields, never a request or authentication state. */
export function RecoveryText({ label, text }: { label: string; text: string }) {
  const id = useId();
  const field = useRef<HTMLTextAreaElement>(null);
  return <section className="form-stack" aria-label={`${label} recovery`}>
    <p id={`${id}-help`}>Select and copy this text to keep your edits before reloading. This copy does not confirm that they were saved.</p>
    <label htmlFor={id}>{label} recovery text</label>
    <textarea id={id} ref={field} readOnly rows={6} value={text} aria-describedby={`${id}-help`} />
    <div className="button-row"><button type="button" className="secondary" onClick={() => { field.current?.focus(); field.current?.select(); }}>Select {label.toLowerCase()} text</button></div>
  </section>;
}
