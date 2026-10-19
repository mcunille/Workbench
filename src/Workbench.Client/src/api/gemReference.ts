import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { apiFetch } from './contract';
import { mutationHeaders } from './auth';

export type GemReferencePageResponse = components['schemas']['GemReferencePageResponse'];
export type GemReferenceDetailResponse = components['schemas']['GemReferenceDetailResponse'];
export type GemReferenceContent = components['schemas']['GemReferenceContent'];
export type GemReferenceSourceContent = components['schemas']['GemReferenceSourceContent'];
export type GemReferenceFieldOverride = components['schemas']['GemReferenceFieldOverride'];
export type GemReferenceEffectiveVersion = components['schemas']['GemReferenceEffectiveVersion'];
export type GemReferenceTenantProblemResponse = components['schemas']['GemReferenceTenantProblemResponse'];
export type GemOrigin = 'tenant' | 'workbench';
export type GemFilters = { query?: string; materialKind?: string; group?: string; cursor?: string; includeArchived?: boolean };

export class GemReferenceApiError extends Error {
  constructor(public readonly status: number, public readonly problem: unknown) {
    super('The gem-reference request could not be completed.');
  }
}

const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin });
function success<T>({ data, error, response }: { data?: T; error?: unknown; response: Response }): T {
  if (!response.ok || data === undefined) throw new GemReferenceApiError(response.status, error);
  return data;
}
export async function browseGems(filters: GemFilters, signal?: AbortSignal): Promise<GemReferencePageResponse> {
  return success(await api.GET('/api/beta/gem-reference', { params: { query: filters }, signal }));
}
export async function getGem(id: string, origin: GemOrigin, signal?: AbortSignal): Promise<GemReferenceDetailResponse> {
  return success(await api.GET('/api/beta/gem-reference/{id}', { params: { path: { id }, query: { origin } }, signal }));
}
export async function createTenantGem(content: GemReferenceContent): Promise<GemReferenceDetailResponse> {
  return success(await api.POST('/api/beta/gem-reference/tenant-entries', { body: content, headers: await mutationHeaders() }));
}
export async function updateTenantGem(id: string, content: GemReferenceContent, effectiveVersion: GemReferenceEffectiveVersion): Promise<GemReferenceDetailResponse> {
  return success(await api.PUT('/api/beta/gem-reference/tenant-entries/{id}', { params: { path: { id } }, body: { content, effectiveVersion }, headers: await mutationHeaders() }));
}
export async function saveGemOverrides(id: string, overrides: Record<string, GemReferenceFieldOverride>, effectiveVersion: GemReferenceEffectiveVersion): Promise<GemReferenceDetailResponse> {
  return success(await api.PUT('/api/beta/gem-reference/{id}/overrides', { params: { path: { id } }, body: { overrides, effectiveVersion }, headers: await mutationHeaders() }));
}
export async function resetGem(id: string, field: string | null, effectiveVersion: GemReferenceEffectiveVersion): Promise<GemReferenceDetailResponse> {
  return success(await api.POST('/api/beta/gem-reference/{id}/reset', { params: { path: { id } }, body: { field, effectiveVersion }, headers: await mutationHeaders() }));
}
export async function setTenantGemArchived(id: string, archived: boolean, effectiveVersion: GemReferenceEffectiveVersion): Promise<GemReferenceDetailResponse> {
  const route = archived ? '/api/beta/gem-reference/tenant-entries/{id}/archive' : '/api/beta/gem-reference/tenant-entries/{id}/restore';
  return success(await api.POST(route, { params: { path: { id } }, body: { effectiveVersion }, headers: await mutationHeaders() }));
}
