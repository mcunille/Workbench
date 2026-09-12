export function formatReferencePrice(value: string | null): string | null {
  if (value === null || !/^\d+(\.\d{1,4})?$/.test(value)) return value;
  const [whole, fraction = ''] = value.split('.');
  return `${whole}.${fraction.replace(/0+$/, '').padEnd(2, '0')}`;
}
