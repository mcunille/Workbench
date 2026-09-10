import type { Acquisition } from '../../api/acquisitions';

export function acquisitionLabel(value: Acquisition) {
  const date = value.year == null ? '' : [String(value.year), ...[value.month, value.day].filter(part => part != null).map(part => String(part).padStart(2, '0'))].join('-');
  return `${value.source ?? 'Source not recorded'} · ${value.method} · ${date || 'Date unknown'} · ${value.id.slice(0, 8)}`;
}

export const methods = [
  'Purchase',
  'Gift',
  'Inheritance',
  'Trade',
  'Other',
  'Unknown',
];
export const sourceLabels: Record<string, string> = {
  Purchase: 'Purchased from',
  Gift: 'Gift from',
  Inheritance: 'Inherited from',
  Trade: 'Traded with',
};
export type Draft = {
  method: string;
  source: string;
  precision: string;
  year: string;
  month: string;
  day: string;
  notes: string;
};
export function fields(value: Acquisition | null): Draft {
  return {
    method: value?.method ?? '',
    source: value?.source ?? '',
    precision:
      value?.day != null
        ? 'Exact date'
        : value?.month != null
          ? 'Month'
          : value?.year != null
            ? 'Year'
            : 'Unknown',
    year: value?.year?.toString() ?? '',
    month: value?.month?.toString() ?? '',
    day: value?.day?.toString() ?? '',
    notes: value?.notes ?? '',
  };
}
