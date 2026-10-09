import { expect, type Page, type APIRequestContext, type Cookie } from './diagnostic-fixture';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import type { GemReferenceContent, GemReferenceDraftResponse } from '../../src/Workbench.Client/src/api/gemReferenceAdmin';

export const library = '/service-admin/gem-reference';
export const adminApi = '/api/beta/service-admin/gem-reference';

const sessions = new Map<number, Cookie[]>();

export async function signInAdmin(page: Page, account = 1, freshSession = false) {
  // Ordinary cases reuse worker-local cookies; revocation cases own a fresh
  // session so they cannot invalidate siblings or exhaust the login budget.
  const cookies = freshSession ? undefined : sessions.get(account);
  if (cookies) {
    await page.context().addCookies(cookies);
    await page.goto(library);
    await expect(page.getByRole('heading', { name: 'Gem reference', exact: true })).toBeVisible();
    return;
  }
  await page.goto('/service-admin/sign-in');
  await expect(page.getByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
  await enterAdminCredentials(page, account, false);
  await expect(page.getByRole('heading', { name: 'Gem reference', exact: true })).toBeVisible();
  if (!freshSession) sessions.set(account, await page.context().cookies());
}

export async function enterAdminCredentials(page: Page, account = 1, cacheSession = true) {
  // Reuse the disposable server's synthetic fixture without logging a password.
  const source = await readFile(new URL('../../scripts/run-browser-server.ps1', import.meta.url), 'utf8');
  const password = source.match(/Set-Content -LiteralPath \$serviceAdminPasswordFile -Value '([^']+)'/)?.[1];
  if (!password) throw new Error('Disposable service-admin fixture is missing.');
  await page.getByLabel('Email', { exact: true }).fill(`browser-service-admin-${account}@example.test`);
  await page.getByLabel('Password', { exact: true }).fill(password);
  const login = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/api/beta/service-admin/auth/login'));
  const identity = page.waitForResponse(response => response.request().method() === 'GET' && response.url().endsWith('/api/beta/service-admin/auth/me'));
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  const response = await login;
  expect(response.status()).toBe(204);
  expect((await identity).status()).toBe(200);
  // A confirmed reauthentication replaces any revoked worker-local session.
  // Fresh sign-in skips this cache until its revocation/recovery journey finishes.
  if (cacheSession) sessions.set(account, await page.context().cookies());
}

export function syntheticContent(name: string): GemReferenceContent {
  return { id: randomUUID(), commonName: name, materialKind: 'mineraloid', group: null, species: null, variety: null, description: null,
    aliases: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null,
    sources: ['commonName', 'materialKind'].map(field => ({ id: randomUUID(), field, title: 'Synthetic browser citation', publisher: 'Browser fixtures', url: null, citation: 'Synthetic acceptance claim; no mineralogical authority asserted.', accessedOn: null, reviewedOn: '2026-10-03' })) };
}

export async function saveApi(request: APIRequestContext, content: GemReferenceContent, draft?: GemReferenceDraftResponse, base: string | null = null) {
  const headers = { 'X-CSRF-TOKEN': (await (await request.get('/api/beta/service-admin/auth/antiforgery')).json()).requestToken };
  const response = await request.put(`${adminApi}/drafts/${draft?.id ?? randomUUID()}`, { headers, data: {
    entryId: content.id, content, expectedDraftRowVersion: draft?.rowVersion ?? null, expectedPublishedRowVersion: base,
  } });
  expect(response.status()).toBe(200);
  return await response.json() as GemReferenceDraftResponse;
}

export async function createThroughUi(page: Page, name: string) {
  await page.getByRole('link', { name: 'New draft', exact: true }).click();
  await page.getByLabel('Common name', { exact: true }).fill(name);
  await page.getByRole('combobox', { name: 'Material kind', exact: true }).selectOption('mineraloid');
  for (const [index, field] of ['commonName', 'materialKind'].entries()) {
    await page.getByRole('button', { name: 'Add source', exact: true }).click();
    const prefix = `Source ${index + 1}`;
    await page.getByRole('combobox', { name: `${prefix} field`, exact: true }).selectOption(field);
    await page.getByLabel(`${prefix} title`, { exact: true }).fill('Synthetic browser citation');
    await page.getByLabel(`${prefix} publisher`, { exact: true }).fill('Browser fixtures');
    await page.getByLabel(`${prefix} publication citation`, { exact: true }).fill('Synthetic acceptance claim; no mineralogical authority asserted.');
    await page.getByLabel(`${prefix} review date`, { exact: true }).fill('2026-10-03');
  }
  return await saveThroughUi(page);
}

export async function saveThroughUi(page: Page) {
  const response = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().includes(`${adminApi}/drafts/`));
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  expect((await response).status()).toBe(200);
  const draft = await (await response).json() as GemReferenceDraftResponse;
  await expect(page.getByRole('status')).toContainText('Draft saved. Publication requires review.');
  return draft;
}

export async function publishThroughUi(page: Page, names: string[]) {
  await page.getByRole('link', { name: 'Gem reference', exact: true }).click();
  await page.getByRole('button', { name: 'Drafts', exact: true }).click();
  for (const name of names) await page.getByRole('checkbox', { name: `Select ${name}`, exact: true }).check();
  await page.getByRole('link', { name: `Review ${names.length} ${names.length === 1 ? 'draft' : 'drafts'}`, exact: true }).click();
  await expect(page.getByRole('region', { name: 'Draft 1 changes', exact: true })).toBeVisible();
  await page.getByRole('checkbox', { name: 'I confirm these reviewed changes should be published.' }).check();
  await page.getByRole('button', { name: `Publish ${names.length} ${names.length === 1 ? 'draft' : 'drafts'}`, exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: `Published ${names.length} shared` })).toBeVisible();
}
