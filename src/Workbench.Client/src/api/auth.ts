import createClient from 'openapi-fetch';
import type { components, paths } from './generated';

export type CurrentIdentity = components['schemas']['CurrentIdentityResponse'];
export type Session = components['schemas']['SessionResponse'];
export type TenantUser = components['schemas']['TenantUserResponse'];

export class ApiError extends Error {
  constructor(public readonly status: number) {
    super('The Workbench request could not be completed.');
  }
}

const api = createClient<paths>({ baseUrl: window.location.origin });
let antiforgeryToken: Promise<string> | undefined;

async function getAntiforgeryToken(): Promise<string> {
  antiforgeryToken ??= api.GET('/api/auth/antiforgery').then(({ data, response }) => {
    if (!response.ok || !data) {
      antiforgeryToken = undefined;
      throw new ApiError(response.status);
    }

    return data.requestToken;
  });
  return antiforgeryToken;
}

async function mutationHeaders(): Promise<Record<string, string>> {
  return { 'X-CSRF-TOKEN': await getAntiforgeryToken() };
}

function requireSuccess(response: Response): void {
  if (!response.ok) {
    throw new ApiError(response.status);
  }
}

function identityChanged(): void {
  antiforgeryToken = undefined;
}

export async function getCurrentIdentity(): Promise<CurrentIdentity | null> {
  const { data, response } = await api.GET('/api/auth/me');
  if (response.status === 401) {
    return null;
  }
  requireSuccess(response);
  if (!data) {
    throw new ApiError(response.status);
  }
  return data;
}

export async function signIn(email: string, password: string): Promise<void> {
  const { response } = await api.POST('/api/auth/login', {
    body: { email, password },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
  identityChanged();
}

export async function signOut(): Promise<void> {
  const { response } = await api.POST('/api/auth/logout', {
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
  identityChanged();
}

export async function changePassword(
  currentPassword: string,
  newPassword: string,
): Promise<void> {
  const { response } = await api.POST('/api/auth/change-password', {
    body: { currentPassword, newPassword },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
  identityChanged();
}

export async function getSessions(): Promise<Session[]> {
  const { data, response } = await api.GET('/api/auth/sessions');
  requireSuccess(response);
  return data ?? [];
}

export async function revokeSession(sessionId: string): Promise<void> {
  const { response } = await api.DELETE('/api/auth/sessions/{sessionId}', {
    params: { path: { sessionId } },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
  identityChanged();
}

export async function revokeAllSessions(): Promise<void> {
  const { response } = await api.DELETE('/api/auth/sessions', {
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
  identityChanged();
}

export async function requestRecovery(email: string): Promise<void> {
  const { response } = await api.POST('/api/auth/recovery', {
    body: { email },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function consumeRecovery(token: string, newPassword: string): Promise<void> {
  const { response } = await api.POST('/api/auth/recovery/consume', {
    body: { token, newPassword },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function consumeInvitation(token: string, newPassword: string): Promise<void> {
  const { response } = await api.POST('/api/auth/invitations/consume', {
    body: { token, newPassword },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function getTenantUsers(): Promise<TenantUser[]> {
  const { data, response } = await api.GET('/api/tenant/users');
  requireSuccess(response);
  return data ?? [];
}

export async function inviteTenantUser(email: string): Promise<void> {
  const { response } = await api.POST('/api/tenant/users/invitations', {
    body: { email },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function disableTenantUser(userId: string): Promise<void> {
  const { response } = await api.DELETE('/api/tenant/users/{userId}', {
    params: { path: { userId } },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function reactivateTenantUser(userId: string): Promise<void> {
  const { response } = await api.POST('/api/tenant/users/{userId}/reactivate', {
    params: { path: { userId } },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function initiateTenantUserRecovery(userId: string): Promise<void> {
  const { response } = await api.POST('/api/tenant/users/{userId}/recovery', {
    params: { path: { userId } },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}

export async function revokeTenantUserSessions(userId: string): Promise<void> {
  const { response } = await api.DELETE('/api/tenant/users/{userId}/sessions', {
    params: { path: { userId } },
    headers: await mutationHeaders(),
  });
  requireSuccess(response);
}
