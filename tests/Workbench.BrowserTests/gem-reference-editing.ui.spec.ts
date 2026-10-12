import { expect, test, type Page } from './diagnostic-fixture';
import { captureEvidence } from './evidence-fixture';
import { newGemDraft } from '../../src/Workbench.Client/src/features/gemReference/gemEditorDraft';
import type { GemReferenceContent } from '../../src/Workbench.Client/src/api/gemReference';

async function identity(page: Page) {
  await page.route('**/api/beta/system', route => route.fulfill({ json: { name: 'Workbench', version: 'synthetic' } }));
  await page.route('**/api/beta/auth/me', route => route.fulfill({ json: { userId: 'synthetic', tenantName: 'Synthetic studio', email: 'synthetic@example.test', permissions: ['TenantAccess'] } }));
  await page.route('**/api/beta/auth/antiforgery', route => route.fulfill({ json: { requestToken: 'synthetic' } }));
}
for (const theme of ['light', 'dark'] as const) test(`tenant keyboard editing, validation retention and long citations in ${theme}`, async ({ page }) => {
  // GIVEN a narrow authenticated editor using the established appearance and long source content.
  await page.setViewportSize({ width: 390, height: 844 }); await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' }); await identity(page);
  await page.route('**/api/beta/gem-reference/tenant-entries', route => route.fulfill({ status: 400, json: { code: 'validation_failed', errors: { commonName: ['Review this synthetic name.'] } } }));
  await page.goto('/gem-reference/new');
  const name = page.getByLabel('Common name', { exact: true }); await name.focus(); await page.keyboard.type('Synthetic personal gem');
  await page.getByLabel('Species', { exact: true }).fill('Synthetic species');
  await page.getByRole('button', { name: 'Add Common name source' }).click();
  await page.getByLabel('Common name source 1 title').fill('Synthetic handbook'); await page.getByLabel('Common name source 1 publisher').fill('Test institute');
  const citation = 'Synthetic publication evidence with long citation text. '.repeat(20);
  await page.getByLabel('Common name source 1 publication citation').fill(citation);
  await page.getByLabel('Common name source 1 review date').fill('2026-10-01');
  // WHEN keyboard submits a rejected draft THEN its citation survives and error recovery is readable.
  await page.getByRole('button', { name: 'Save changes', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('alert')).toContainText('Review this synthetic name.');
  await expect(page.getByLabel('Common name source 1 publication citation')).toHaveValue(citation);
  await expect(page.getByRole('alert')).toBeFocused();
  await captureEvidence(page, `gem-reference/editor-${theme}-390.png`, { fullPage: true });
  // AND enlarged text at 320px does not create horizontal scrolling or cover the final controls.
  await page.setViewportSize({ width: 320, height: 844 }); await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  const save = page.getByRole('button', { name: 'Save changes', exact: true }); await save.scrollIntoViewIfNeeded();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const box = await save.boundingBox(); expect(box!.height).toBeGreaterThanOrEqual(44);
});

test('uncertain creation checks the original tenant ID and offers explicit recovery', async ({ page }) => {
  // GIVEN the first creation committed but its response was lost.
  await identity(page); let saved: GemReferenceContent = newGemDraft('placeholder').content; let posts = 0;
  await page.route('**/api/beta/gem-reference/tenant-entries', async route => { posts++; saved = route.request().postDataJSON(); await route.abort('failed'); });
  await page.route('**/api/beta/gem-reference/*?origin=tenant', route => route.fulfill({ json: { ...saved, origin: 'tenant', layer: 'tenantEntry', rowVersion: 'v1', effectiveVersion: { sharedRowVersion: null, tenantRowVersion: 'v1' }, retirement: { isRetired: false, explanation: null, redirectEntryId: null }, sourceAssertions: [] } }));
  await page.goto('/gem-reference/new'); await page.getByLabel('Common name', { exact: true }).fill('Synthetic uncertain gem'); await page.getByLabel('Species', { exact: true }).fill('Synthetic species');
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  // WHEN checking THEN saved and draft values can be reviewed without another POST.
  await page.getByRole('button', { name: 'Check current entry', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Review current entry', exact: true })).toContainText('Synthetic uncertain gem');
  expect(posts).toBe(1);
  await page.getByRole('button', { name: 'Use current entry', exact: true }).click();
  await expect(page.getByLabel('Common name', { exact: true })).toHaveValue('Synthetic uncertain gem');
});
