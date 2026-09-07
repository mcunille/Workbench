import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type CreateItemRequest = components['schemas']['CreateItemRequest'];
export type ItemDetail = components['schemas']['ItemDetailResponse'];
export type ItemPage = components['schemas']['ItemPageResponse'];
export class ItemValidationError extends ApiError {
  constructor(public readonly errors: Record<string, string[]>) {
    super(400);
  }
}
const api = createClient<paths>({ baseUrl: window.location.origin });
export async function createItem(body: CreateItemRequest): Promise<ItemDetail> {
  const { data, response, error } = await api.POST('/api/items', {
    body,
    headers: await mutationHeaders(),
  });
  if (response.status === 400 && error && 'errors' in error && error.errors)
    throw new ItemValidationError(error.errors);
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function getItems(cursor?: string): Promise<ItemPage> {
  const { data, response } = await api.GET('/api/items', {
    params: { query: { cursor } },
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function getItem(id: string): Promise<ItemDetail> {
  const { data, response } = await api.GET('/api/items/{id}', {
    params: { path: { id } },
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
