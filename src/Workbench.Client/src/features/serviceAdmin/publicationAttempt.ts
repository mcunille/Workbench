import type { GemReferencePublishRequest } from '../../api/gemReferenceAdmin';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const key = (accountId: string) => `workbench:gem-publication:v1:${encodeURIComponent(accountId)}`;
function requestShape(value: unknown): GemReferencePublishRequest | null {
  if (!value || typeof value !== 'object' || !('requestId' in value) || typeof value.requestId !== 'string' || !uuid.test(value.requestId) || !('drafts' in value) || !Array.isArray(value.drafts) || value.drafts.length < 1 || value.drafts.length > 50) return null;
  const ids = new Set<string>();
  const drafts = [];
  for (const item of value.drafts) {
    if (!item || typeof item !== 'object' || typeof item.draftId !== 'string' || !uuid.test(item.draftId) || typeof item.expectedDraftRowVersion !== 'string' || !item.expectedDraftRowVersion.trim() || ids.has(item.draftId.toLowerCase())) return null;
    ids.add(item.draftId.toLowerCase());
    drafts.push({ draftId: item.draftId, expectedDraftRowVersion: item.expectedDraftRowVersion });
  }
  return { requestId: value.requestId, drafts };
}
export function readPendingPublication(accountId: string): GemReferencePublishRequest | null {
  const stored = sessionStorage.getItem(key(accountId));
  if (!stored) return null;
  try { return requestShape(JSON.parse(stored)); } catch { return null; }
}
export function writePendingPublication(accountId: string, request: GemReferencePublishRequest): void {
  const safe = requestShape(request);
  if (!safe) throw new Error('Invalid publication identity or selection.');
  sessionStorage.setItem(key(accountId), JSON.stringify(safe));
}
export function clearPendingPublication(accountId: string): void { sessionStorage.removeItem(key(accountId)); }
