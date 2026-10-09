import type { MouseEvent } from 'react';
import type { GemOrigin } from '../../api/gemReference';

export const materialKinds = { mineral: 'Mineral', mineraloid: 'Mineraloid', organic: 'Organic', rockAggregate: 'Rock / aggregate' };
export const fieldLabels: Record<string, string> = { commonName: 'Common name', materialKind: 'Material kind', group: 'Group', species: 'Species', variety: 'Variety', aliases: 'Aliases', description: 'Description', notableLocality: 'Notable locality' };
export function layerLabel(layer: string) {
  return { workbenchReference: 'Workbench reference', workbenchReferenceCustomized: 'Workbench reference · customized', tenantEntry: 'Tenant entry' }[layer] ?? 'Reference';
}
export function gemPath(id: string, origin: string = 'workbench') {
  return `/gem-reference/${origin === 'tenant' ? 'tenant' : 'workbench'}/${encodeURIComponent(id)}`;
}
export type GemNavigation = { follow: (event: MouseEvent<HTMLAnchorElement>) => void; onAuthLost: () => void };
export type GemIdentity = { id: string; origin: GemOrigin };
