import type { Acquisition } from '../../api/acquisitions';

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
