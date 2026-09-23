import { apiFetch } from './contract';
import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type AccountingRole = components['schemas']['AccountingRoleResponse'];
export type RoleAssignment = components['schemas']['AccountingRoleAssignmentResponse'];
const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin, cache: 'no-store' });
function result<T>({ response, data }: { response: Response; data?: T }): T { if (!response.ok || !data) throw new ApiError(response.status); return data; }
export async function getAccountingRoles() { return result(await api.GET('/api/beta/tenant/accounting-roles')); }
export async function getRoleAssignment(userId: string) { return result(await api.GET('/api/beta/tenant/users/{userId}/accounting-roles', { params: { path: { userId } } })); }
export async function saveRoleAssignment(userId: string, body: components['schemas']['AccountingRoleAssignmentRequest']) { return result(await api.POST('/api/beta/tenant/users/{userId}/accounting-roles', { params: { path: { userId } }, body, headers: await mutationHeaders() })); }
