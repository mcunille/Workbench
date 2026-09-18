export function SafeSourceLink({ value }: { value: string }) {
  let safe = false;
  try {
    const url = new URL(value);
    safe = ['http:', 'https:'].includes(url.protocol) && !!url.hostname && !url.username && !url.password;
  } catch { /* Preserve archived source text even when it is not a URL. */ }
  return safe ? <a href={value} target="_blank" rel="noopener noreferrer">{value}</a> : <span>{value}</span>;
}
