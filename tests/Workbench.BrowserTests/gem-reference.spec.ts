import { randomUUID } from 'node:crypto';
import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { captureEvidence } from './evidence-fixture';
import type { GemReferenceDetailResponse, GemReferencePageResponse } from '../../src/Workbench.Client/src/api/gemReference';

test('browses the pilot and each tenant’s effective reference through real APIs', async ({ page, browser }) => {
  // GIVEN two independently authenticated tenants and the installed reviewed pilot.
  await useAuthenticatedSession(page);
  const otherContext = await browser.newContext();
  const other = await otherContext.newPage();
  let ruby: GemReferenceDetailResponse | undefined;
  let csrf: string | undefined;
  try {
    await useAuthenticatedSession(other, 'secondary', 'auth');
    await page.getByRole('link', { name: 'Gem reference', exact: true }).click();
    for (const [name, classification] of [['Diamond', 'Diamond'], ['Sapphire', 'Corundum'], ['Emerald', 'Beryl'], ['Ruby', 'Corundum']]) {
      // WHEN searching by common name and classification THEN each pilot is findable.
      for (const query of [name, classification]) {
        await page.getByLabel('Search gems', { exact: true }).fill(query);
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await expect(page.getByRole('link', { name, exact: true })).toBeVisible();
      }
    }
    const results = await (await page.request.get('/api/beta/gem-reference?query=Ruby')).json() as GemReferencePageResponse;
    const row = results.entries.find(entry => entry.commonName === 'Ruby' && entry.origin === 'workbench')!;
    ruby = await (await page.request.get(`/api/beta/gem-reference/${row.id}?origin=workbench`)).json() as GemReferenceDetailResponse;
    csrf = (await (await page.request.get('/api/beta/auth/antiforgery')).json()).requestToken;
    const headers = { 'X-CSRF-TOKEN': csrf! };
    const privateVariety = `Synthetic private variety ${randomUUID().slice(0, 8)}`;
    // AND a real sparse override replaces only this tenant's variety.
    const overridden = await page.request.put(`/api/beta/gem-reference/${ruby.id}/overrides`, { headers, data: { effectiveVersion: ruby.effectiveVersion, overrides: { variety: { state: 'replace', value: privateVariety, sources: [] } } } });
    expect(overridden.status()).toBe(200);
    const privateName = `Synthetic tenant gem ${randomUUID().slice(0, 8)}`;
    const privateId = randomUUID();
    const addition = await page.request.post('/api/beta/gem-reference/tenant-entries', { headers, data: { id: privateId, commonName: privateName, materialKind: 'organic', group: null, species: null, variety: null, description: null, aliases: [], notableLocality: null, sources: [], isRetired: false, retirementExplanation: null, redirectEntryId: null } });
    expect(addition.status()).toBe(200);
    // WHEN browsing both origins THEN their details remain distinct and sourced correctly.
    await page.goto('/gem-reference');
    await page.getByLabel('Search gems', { exact: true }).fill('Ruby');
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByRole('link', { name: 'Ruby', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Ruby', exact: true })).toBeVisible();
    const variety = page.locator('dt').filter({ hasText: /^Variety$/ }).locator('..');
    await expect(variety).toContainText(privateVariety);
    await expect(variety).toContainText('Tenant-authored · no sources supplied');
    await expect(page.getByRole('region', { name: 'Sources', exact: true })).toContainText('Workbench source');
    await expect(page.getByRole('region', { name: 'Sources', exact: true })).not.toContainText('Variety · Workbench source');
    await captureEvidence(page, 'gem-reference/live-customized-desktop.png', { fullPage: true });
    await page.getByRole('link', { name: 'Back to gem reference' }).click();
    await expect(page.getByLabel('Search gems', { exact: true })).toHaveValue('Ruby');
    await page.getByLabel('Search gems', { exact: true }).fill(privateName);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    const privateLink = page.getByRole('link', { name: privateName, exact: true });
    await expect(privateLink).toHaveAttribute('href', `/gem-reference/tenant/${privateId}`);
    await privateLink.click();
    await expect(page.getByRole('heading', { name: privateName, exact: true })).toBeVisible();
    await expect(page.getByText('Mineral species is not required for this material kind.')).toBeVisible();
    // THEN the other tenant's UI receives the original shared value and no private addition.
    await other.goto(`/gem-reference/workbench/${ruby.id}`);
    const otherVariety = other.locator('dt').filter({ hasText: /^Variety$/ }).locator('..');
    await expect(otherVariety).toContainText('Workbench reference');
    await expect(otherVariety).not.toContainText(privateVariety);
    await other.getByRole('link', { name: 'Back to gem reference' }).click();
    await other.getByLabel('Search gems', { exact: true }).fill(privateName);
    await other.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(other.getByText('No gems match these filters.')).toBeVisible();
    expect((await other.request.get(`/api/beta/gem-reference/${privateId}?origin=tenant`)).status()).toBe(404);
  } finally {
    if (ruby && csrf) {
      const current = await page.request.get(`/api/beta/gem-reference/${ruby.id}?origin=workbench`);
      if (current.ok()) expect((await page.request.post(`/api/beta/gem-reference/${ruby.id}/reset`, { headers: { 'X-CSRF-TOKEN': csrf }, data: { field: 'variety', effectiveVersion: (await current.json()).effectiveVersion } })).status()).toBe(200);
    }
    await otherContext.close();
  }
});
