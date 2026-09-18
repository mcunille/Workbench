// Resolve fetch at call time so generated and direct callers share the active browser transport.
export function apiFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  return fetch(input, init);
}
