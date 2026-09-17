import { apiFetch } from './contract';
import createClient from 'openapi-fetch';
import type { components, paths } from './generated';

export type SystemInformation = components['schemas']['SystemResponse'];

const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin });

export async function getSystem(): Promise<SystemInformation> {
  const { data, error, response } = await api.GET('/api/beta/system');

  if (!response.ok || error || !data) {
    throw new Error('The system endpoint did not return a successful response.');
  }

  return data;
}
