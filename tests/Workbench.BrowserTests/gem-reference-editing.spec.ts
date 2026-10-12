import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { captureEvidence } from './evidence-fixture';
import { adminApi, saveApi, signInAdmin, syntheticContent } from './service-admin-fixture';
import type { GemReferenceDetailResponse, GemReferenceContent } from '../../src/Workbench.Client/src/api/gemReference';

async function saveTenant(page: Page, method = 'PUT') {
  const pending = page.waitForResponse(response => response.request().method() === method && response.url().includes('/api/beta/gem-reference/'));
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  const response = await pending; expect(response.status()).toBe(200);
  const entry = await response.json() as GemReferenceDetailResponse;
  await expect(page.getByRole('heading', { name: entry.commonName, exact: true })).toBeVisible();
  return entry;
}
async function publish(page: Page, content: GemReferenceContent, base: string | null = null) {
  const draft = await saveApi(page.request, content, undefined, base);
  const token = (await (await page.request.get('/api/beta/service-admin/auth/antiforgery')).json()).requestToken;
  const response = await page.request.post(`${adminApi}/publish`, { headers: { 'X-CSRF-TOKEN': token }, data: { requestId: randomUUID(), drafts: [{ draftId: draft.id, expectedDraftRowVersion: draft.rowVersion }] } });
  expect(response.status()).toBe(200);
  return (await (await page.request.get(`${adminApi}/${content.id}`)).json()) as GemReferenceDetailResponse;
}

test('tenant creates, refreshes, resolves a concurrent edit and archives/restores through the UI', async ({ page }) => {
  // GIVEN an authenticated member creating an unsourced personal reference.
  await useAuthenticatedSession(page);
  const name = `Synthetic tenant edit ${randomUUID().slice(0, 8)}`;
  await page.goto('/gem-reference'); await page.getByRole('link', { name: 'Add entry', exact: true }).click();
  await page.getByLabel('Common name', { exact: true }).fill(name);
  await page.getByRole('combobox', { name: 'Material kind', exact: true }).selectOption('organic');
  await page.getByRole('textbox', { name: 'Description', exact: true }).fill('Personal reference notes');
  const saved = await saveTenant(page, 'POST');
  await page.reload(); await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(page.locator('dt').filter({ hasText: /^Description$/ }).locator('..')).toContainText('Tenant-authored · no sources supplied');
  // WHEN two browser pages edit the same saved entry THEN the stale draft is preserved for explicit reconciliation.
  const competitor = await page.context().newPage();
  try {
    await page.getByRole('link', { name: 'Edit entry', exact: true }).click();
    await expect(page.getByRole('textbox', { name: 'Description', exact: true })).toHaveValue('Personal reference notes');
    await competitor.goto(`/gem-reference/tenant/${saved.id}/edit`);
    await expect(competitor.getByRole('textbox', { name: 'Description', exact: true })).toHaveValue('Personal reference notes');
    await page.getByRole('textbox', { name: 'Description', exact: true }).fill('First member saved note'); await saveTenant(page);
    await competitor.getByRole('textbox', { name: 'Description', exact: true }).fill('Second member retained note');
    await competitor.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(competitor.getByRole('region', { name: 'Review current entry', exact: true })).toContainText('First member saved note');
    await expect(competitor.getByRole('textbox', { name: 'Description', exact: true })).toHaveValue('Second member retained note');
    await competitor.getByRole('button', { name: 'Keep my draft with current versions' }).click(); await saveTenant(competitor);
  } finally { await competitor.close(); }
  await page.reload();
  await expect(page.locator('dt').filter({ hasText: /^Description$/ }).locator('..')).toContainText('Second member retained note');
  await captureEvidence(page, 'gem-reference/live-tenant-edited.png', { fullPage: true });
  // WHEN archiving and searching THEN ordinary results hide the entry and inclusive results retain its stable detail.
  await page.getByRole('button', { name: 'Archive tenant entry' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Archive entry', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Restore tenant entry' })).toBeVisible();
  await page.getByRole('link', { name: 'Back to gem reference' }).click();
  await page.getByLabel('Search gems', { exact: true }).fill(name); await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByText('No gems match these filters.')).toBeVisible();
  await page.getByLabel('Include archived tenant entries').check(); await page.getByRole('button', { name: 'Search', exact: true }).click();
  await page.getByRole('link', { name, exact: true }).click();
  await page.getByRole('button', { name: 'Restore tenant entry' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Restore entry', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Archive tenant entry' })).toBeVisible();
  await page.reload(); await expect(page.getByText('Archived tenant entry', { exact: true })).toHaveCount(0);
});

test('tenant reconciles an upstream classification and source correction while another tenant remains unaffected', async ({ page, browser }) => {
  // GIVEN a sourced synthetic shared entry, an explicit tenant clear, and an independent tenant session.
  await page.emulateMedia({ colorScheme: 'dark' });
  await useAuthenticatedSession(page);
  const adminContext = await browser.newContext(); const otherContext = await browser.newContext();
  try {
    const admin = await adminContext.newPage(); const other = await otherContext.newPage();
    await signInAdmin(admin); await useAuthenticatedSession(other, 'secondary', 'auth');
    const content = syntheticContent(`Synthetic reconcile ${randomUUID().slice(0, 8)}`);
    const original = await publish(admin, content);
    const path = `/gem-reference/workbench/${content.id}`;
    await page.goto(path); await page.getByRole('link', { name: 'Customize for this tenant' }).click();
    await page.getByRole('combobox', { name: 'Species choice', exact: true }).selectOption('clear');
    await page.getByRole('combobox', { name: 'Description choice', exact: true }).selectOption('replace'); await page.getByRole('textbox', { name: 'Description', exact: true }).fill('Retained personal note');
    await saveTenant(page);
    // WHEN shared publication requires species and changes its source THEN the tenant's retained clear becomes Needs review.
    await publish(admin, { ...content, materialKind: 'mineral', species: 'Synthetic species', sources: [...content.sources, { id: randomUUID(), field: 'species', title: 'Corrected synthetic classification', publisher: 'Browser fixtures', url: null, citation: 'Synthetic current classification evidence.', reviewedOn: '2026-10-04', accessedOn: null }] }, original.rowVersion);
    await page.reload(); await expect(page.getByRole('region', { name: 'Needs review', exact: true })).toBeVisible();
    await expect(page.getByText('No validated classification is available.')).toBeVisible();
    await page.getByRole('link', { name: 'Customize for this tenant' }).click();
    await expect(page.getByRole('region', { name: 'Species editor' })).toContainText('Synthetic species');
    await expect(page.getByRole('combobox', { name: 'Species choice', exact: true })).toHaveValue('clear');
    await page.getByRole('textbox', { name: 'Description', exact: true }).fill('Unrelated edit retained during repair');
    const invalid = page.waitForResponse(response => response.request().method() === 'PUT' && response.url().endsWith('/overrides'));
    await page.getByRole('button', { name: 'Save changes', exact: true }).click(); expect((await invalid).status()).toBe(409);
    await expect(page.getByRole('textbox', { name: 'Description', exact: true })).toHaveValue('Unrelated edit retained during repair');
    // WHEN explicitly resetting the conflicting species THEN current shared value and sources return together.
    await page.getByRole('button', { name: 'Reset Species to Workbench' }).click(); await saveTenant(page);
    await page.reload();
    const species = page.locator('dt').filter({ hasText: /^Species$/ }).locator('..');
    await expect(species).toContainText('Synthetic species'); await expect(species).toContainText('Workbench reference');
    await expect(page.getByRole('region', { name: 'Sources', exact: true })).toContainText('Corrected synthetic classification');
    await captureEvidence(page, 'gem-reference/live-reconciled-dark.png', { fullPage: true });
    // THEN another tenant has current shared classification with none of this tenant's choices.
    await other.goto(path); await expect(other.getByRole('region', { name: 'Needs review', exact: true })).toHaveCount(0);
    await expect(other.locator('dt').filter({ hasText: /^Description$/ }).locator('..')).not.toContainText('Unrelated edit');
    await page.getByRole('link', { name: 'Customize for this tenant' }).click();
    await page.getByRole('button', { name: 'Reset all fields to Workbench' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Reset all fields', exact: true }).click();
    await expect(page.getByRole('heading', { name: content.commonName, exact: true })).toBeVisible();
    await expect(page.getByText('Workbench reference', { exact: true }).first()).toBeVisible();
  } finally { await Promise.allSettled([adminContext.close(), otherContext.close()]); }
});
