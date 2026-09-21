export function SupplierProfileLink({ label, value }: { label: string; value: string }) {
  try {
    const url = new URL(value);
    if (value.length > 2048 || /[\s\u0000-\u001f\u007f-\u009f\\]/u.test(value) || !['http:', 'https:'].includes(url.protocol) || !url.hostname || url.username || url.password) return null;
  } catch { return null; }
  return <a className="po-field-help" href={value} target="_blank" rel="noopener noreferrer">Open {label} profile (new tab)</a>;
}
