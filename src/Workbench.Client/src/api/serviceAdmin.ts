import type { components } from './generated';
import createClient from 'openapi-fetch';
import type { paths } from './generated';
import { apiFetch } from './contract';

export type CurrentServiceAdminResponse = components['schemas']['CurrentServiceAdminResponse'];

export class ServiceAdminApiError extends Error {
  constructor(public readonly status: number) {
    super('The service-admin request could not be completed.');
  }
}

const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin });
let antiforgeryToken: Promise<string> | undefined;
let accountId: string | null = null;

function requireSuccess(response: Response): void {
  if (!response.ok) throw new ServiceAdminApiError(response.status);
}

export async function serviceAdminMutationHeaders(): Promise<Record<string, string>> {
  // Admin antiforgery is bound to admin identity, independently of tenant cookies.
  antiforgeryToken ??= api.GET('/api/beta/service-admin/auth/antiforgery')
    .then(({ data, response }) => {
      requireSuccess(response);
      if (!data) throw new ServiceAdminApiError(response.status);
      return data.requestToken;
    }).catch((error: unknown) => {
      antiforgeryToken = undefined;
      throw error;
    });
  return { 'X-CSRF-TOKEN': await antiforgeryToken };
}

export async function getServiceAdminIdentity(): Promise<CurrentServiceAdminResponse | null> {
  const { data, response } = await api.GET('/api/beta/service-admin/auth/me');
  if (response.status === 401) {
    accountId = null;
    antiforgeryToken = undefined;
    return null;
  }
  requireSuccess(response);
  if (!data) throw new ServiceAdminApiError(response.status);
  if (accountId !== data.accountId) antiforgeryToken = undefined;
  accountId = data.accountId;
  return data;
}

export async function signInServiceAdmin(email: string, password: string): Promise<void> {
  const { response } = await api.POST('/api/beta/service-admin/auth/login', {
    body: { email, password }, headers: await serviceAdminMutationHeaders(),
  });
  requireSuccess(response);
  antiforgeryToken = undefined;
}

export async function signOutServiceAdmin(): Promise<void> {
  const { response } = await api.POST('/api/beta/service-admin/auth/logout', {
    headers: await serviceAdminMutationHeaders(),
  });
  requireSuccess(response);
  accountId = null;
  antiforgeryToken = undefined;
}
