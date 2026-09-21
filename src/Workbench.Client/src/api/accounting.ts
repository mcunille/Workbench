import { apiFetch } from './contract';
import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type Configuration = components['schemas']['AccountingConfiguration'];
export type Policies = components['schemas']['AccountingPolicies'];
export type Coverage = components['schemas']['AccountingCoverage'];
export type Account = components['schemas']['AccountingAccountResponse'];
export type AccountContent = components['schemas']['AccountingAccountContent'];
export type Setup = components['schemas']['AccountingSetupResponse'];
export type Catalog = components['schemas']['AccountingCatalogResponse'];
export class AccountingError extends ApiError {
  constructor(status: number, public readonly detail: string, public readonly errors: Record<string, string[]> = {}) { super(status); }
}
const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin, cache: 'no-store', credentials: 'same-origin' });
function result<T>({ response, data, error }: { response: Response; data?: T; error?: unknown }): T {
  if (!response.ok || data === undefined) {
    const problem = error && typeof error === 'object' ? error as { detail?: string; errors?: Record<string, string[]> } : {};
    throw new AccountingError(response.status, problem.detail ?? 'The accounting request could not be completed.', problem.errors ?? {});
  }
  return data;
}
export async function getAccountingCatalog() { return result(await api.GET('/api/beta/accounting/catalog')); }
export async function getAccountingSetup() { return result(await api.GET('/api/beta/accounting/setup')); }
export async function saveAccountingSetup(body: components['schemas']['SaveAccountingConfigurationRequest']) { return result(await api.PUT('/api/beta/accounting/setup', { body, headers: await mutationHeaders() })); }
export async function getAccounts(cursor?: string, query?: string, includeArchived = false) { return result(await api.GET('/api/beta/accounting/accounts', { params: { query: { cursor, query, includeArchived } } })); }
export async function createAccounts(body: components['schemas']['CreateAccountingAccountsRequest']) { return result(await api.POST('/api/beta/accounting/accounts', { body, headers: await mutationHeaders() })); }
export async function updateAccount(id: string, body: components['schemas']['UpdateAccountingAccountRequest']) { return result(await api.PUT('/api/beta/accounting/accounts/{id}', { params: { path: { id } }, body, headers: await mutationHeaders() })); }
export async function archiveAccount(id: string, body: components['schemas']['ArchiveAccountingAccountRequest']) { return result(await api.POST('/api/beta/accounting/accounts/{id}/archive', { params: { path: { id } }, body, headers: await mutationHeaders() })); }
