import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { apiFetch } from './contract';

export type GemReferencePageResponse = components['schemas']['GemReferencePageResponse'];
export type GemReferenceDetailResponse = components['schemas']['GemReferenceDetailResponse'];
export type GemOrigin = 'tenant' | 'workbench';
export type GemFilters = { query?: string; materialKind?: string; group?: string; cursor?: string };

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
