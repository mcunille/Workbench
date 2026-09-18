export const API_REVISION = 'beta-3';

export class ApiContractUnsupportedError extends Error {
  readonly code = 'api_contract_unsupported';
  constructor() {
    super('Workbench has been updated. Copy your unsaved changes before reloading.');
  }
}

export function createApiTransport() {
  let reloadRequired = false;
  const listeners = new Set<() => void>();
  return {
    async fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
      const request = typeof input === 'object' && 'method' in input ? input : undefined;
      const method = (init?.method ?? request?.method ?? 'GET').toUpperCase();
      if (reloadRequired && !['GET', 'HEAD', 'OPTIONS'].includes(method)) throw new ApiContractUnsupportedError();
      const headers = new Headers(init?.headers ?? (request ? Array.from(request.headers.entries()) : undefined));
      headers.set('X-Workbench-Api-Revision', API_REVISION);
      const response = await fetch(input, { ...init, headers });
      if (response.status === 409) {
        const problem: unknown = await response.clone().json().catch(() => null);
        if (problem && typeof problem === 'object' && 'code' in problem && problem.code === 'api_contract_unsupported') {
          if (!reloadRequired) {
            reloadRequired = true;
            listeners.forEach(listener => listener());
          }
          // Keep uncertain submissions on the recovery path; ordinary 4xx handling may discard their request identity.
          throw new ApiContractUnsupportedError();
        }
      }
      return response;
    },
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    getSnapshot: () => reloadRequired,
  };
}

export const apiContract = createApiTransport();
export const apiFetch = apiContract.fetch;
