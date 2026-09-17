/** Human-readable text from explicitly supplied form state, without touching the DOM. */
export function recoveryText(fields: object): string {
  return Object.entries(fields).filter(([, value]) => value !== null && value !== undefined && value !== '').map(([key, value]) => {
    const label = key.replace(/([a-z])([A-Z])/g, '$1 $2');
    const heading = label.charAt(0).toUpperCase() + label.slice(1);
    if (Array.isArray(value)) return value.length ? `${heading}:\n${value.map((entry: unknown, index) => `${index + 1}. ${typeof entry === 'object' && entry !== null ? recoveryText(entry) : String(entry)}`).join('\n\n')}` : '';
    return `${heading}: ${typeof value === 'object' ? `\n${recoveryText(value)}` : String(value)}`;
  }).filter(Boolean).join('\n');
}
