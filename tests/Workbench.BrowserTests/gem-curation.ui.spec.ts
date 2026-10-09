import { expect, test, type Page } from './diagnostic-fixture';
import { adminApi, library, syntheticContent } from './service-admin-fixture';
import { captureEvidence } from './evidence-fixture';
import type { GemReferenceDraftResponse, GemReferencePublishRequest } from '../../src/Workbench.Client/src/api/gemReferenceAdmin';

const accountId = '11111111-1111-4111-8111-111111111111';
function draftFixture(): GemReferenceDraftResponse {
  const content = syntheticContent('Synthetic narrow reference');
  content.sources[0].citation = 'Citation'.repeat(150);
  return { id: '22222222-2222-4222-8222-222222222222', entryId: content.id, content, expectedPublishedRowVersion: null, rowVersion: 'draft-v1', createdBy: accountId, updatedBy: accountId, createdAtUtc: '2026-10-03T12:00:00Z', updatedAtUtc: '2026-10-03T12:00:00Z', errors: {} };
}
async function installAdmin(page: Page) {
  await page.route('**/api/beta/system', route => route.fulfill({ json: { name: 'Workbench', version: 'test' } }));
  await page.route('**/api/beta/service-admin/auth/me', route => route.fulfill({ json: { accountId, email: 'synthetic-curator@example.test' } }));
  await page.route('**/api/beta/service-admin/auth/antiforgery', route => route.fulfill({ json: { requestToken: 'synthetic-admin-csrf' } }));
}
async function assertFits(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
}

for (const theme of ['light', 'dark'] as const) test(`keyboard editor and combined review stay readable at 390px in ${theme}`, async ({ page }) => {
  // GIVEN owned routes registered before navigation and a narrow appearance-specific browser.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' });
  await installAdmin(page);
  let draft = draftFixture(), saves = 0;
  await page.route(`**${adminApi}`, route => route.fulfill({ json: { entries: [], nextCursor: null } }));
  await page.route(`**${adminApi}/drafts`, route => route.fulfill({ json: { drafts: [draft], nextCursor: null } }));
  await page.route(`**${adminApi}/drafts/${draft.id}`, async route => {
    if (route.request().method() === 'PUT') {
      draft = { ...draft, content: route.request().postDataJSON().content, rowVersion: `draft-v${++saves + 1}`, errors: saves === 1 ? { sources: ['Synthetic citation needs editorial review.'] } : {} };
    }
    await route.fulfill({ json: draft });
  });
  await page.route(`**${adminApi}/review`, route => route.fulfill({ json: { entries: [{ draftId: draft.id, entryId: draft.entryId, isStale: false, errors: {}, changes: [
    { field: 'commonName', before: 'Previous name', after: draft.content.commonName }, { field: 'sources', before: [], after: draft.content.sources },
  ] }] } }));
  await page.goto(`${library}/drafts/${draft.id}`);
  await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
  // WHEN editing source controls through keyboard THEN the draft retains corrections and dirty navigation is cancellable.
  const title = page.getByLabel('Source 1 title', { exact: true });
  await title.focus();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.type('Keyboard edited citation');
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Source 1 publisher', { exact: true })).toBeFocused();
  await page.getByRole('link', { name: 'Gem reference', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(title).toHaveValue('Keyboard edited citation');
  await page.getByRole('button', { name: 'Save draft', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('alert')).toContainText('Synthetic citation needs editorial review.');
  await expect(page.getByRole('alert')).toBeFocused();
  await expect(title).toHaveValue('Keyboard edited citation');
  await assertFits(page);
  await captureEvidence(page, `gem-curation/editor-390-${theme}.png`, { fullPage: true });
  // AND correcting the cited claim retains the authoritative issue until a confirmed new save.
  await page.getByRole('textbox', { name: 'Source 1 publication citation', exact: true }).fill(`${draft.content.sources[0].citation} corrected`);
  await expect(page.getByRole('alert')).toContainText('Synthetic citation needs editorial review.');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  // WHEN selecting and reviewing by keyboard THEN long citations wrap and confirmation stays reachable.
  await page.getByRole('link', { name: 'Gem reference', exact: true }).click();
  await page.getByRole('button', { name: 'Drafts', exact: true }).click();
  const selection = page.getByRole('checkbox', { name: `Select ${draft.content.commonName}`, exact: true });
  await selection.focus();
  await page.keyboard.press('Space');
  await expect(selection).toBeChecked();
  await page.getByRole('link', { name: 'Review 1 draft', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('region', { name: 'Sources after', exact: true })).toContainText(draft.content.sources[0].citation!);
  await expect(page.getByRole('region', { name: 'Common name before', exact: true })).toContainText('Previous name');
  await expect(page.getByRole('link', { name: 'Keyboard edited citation' })).toHaveCount(0);
  const confirmation = page.getByRole('checkbox', { name: 'I confirm these reviewed changes should be published.' });
  await confirmation.focus();
  await page.keyboard.press('Space');
  await expect(page.getByRole('button', { name: 'Publish 1 draft', exact: true })).toBeEnabled();
  await assertFits(page);
  const before = (await page.getByRole('region', { name: 'Common name before', exact: true }).boundingBox())!;
  const after = (await page.getByRole('region', { name: 'Common name after', exact: true }).boundingBox())!;
  expect(after.y).toBeGreaterThan(before.y);
  await captureEvidence(page, `gem-curation/review-390-${theme}.png`, { fullPage: true });
});

test('failed load retries and a lost publication response recovers its durable outcome after reload', async ({ page }) => {
  // GIVEN an unavailable catalog and owned draft/review/publication responses installed before navigation.
  await installAdmin(page);
  const draft = draftFixture();
  let loads = 0, request: GemReferencePublishRequest | undefined;
  await page.route(`**${adminApi}`, route => ++loads === 1 ? route.fulfill({ status: 503, json: {} }) : route.fulfill({ json: { entries: [], nextCursor: null } }));
  await page.route(`**${adminApi}/drafts`, route => route.fulfill({ json: { drafts: [draft], nextCursor: null } }));
  const entry = { draftId: draft.id, entryId: draft.entryId, isStale: false, errors: {}, changes: [{ field: 'commonName', before: null, after: draft.content.commonName }] };
  await page.route(`**${adminApi}/review`, route => route.fulfill({ json: { entries: [entry] } }));
  await page.route(`**${adminApi}/publish`, async route => { request = route.request().postDataJSON(); await route.abort('failed'); });
  await page.route(`**${adminApi}/publications/*`, route => {
    expect(request).toBeDefined();
    expect(new URL(route.request().url()).pathname.endsWith(request!.requestId)).toBe(true);
    return route.fulfill({ json: { requestId: request!.requestId, code: 'published', entries: [{ entryId: draft.entryId, rowVersion: 'published-v1' }], review: [{ ...entry, changes: [{ field: 'commonName', before: null, after: null }] }] } });
  });
  await page.goto(library);
  await expect(page.getByRole('alert')).toContainText('We could not load the catalog.');
  // WHEN retrying the read THEN the library remains usable for the selected publication.
  await page.getByRole('button', { name: 'Retry catalog', exact: true }).click();
  await expect(page.getByText('No published entries yet.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Drafts', exact: true }).click();
  await page.getByRole('checkbox', { name: `Select ${draft.content.commonName}`, exact: true }).check();
  await page.getByRole('link', { name: 'Review 1 draft', exact: true }).click();
  await page.getByRole('checkbox', { name: 'I confirm these reviewed changes should be published.' }).check();
  await page.getByRole('button', { name: 'Publish 1 draft', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('The publication outcome is uncertain.');
  const retained = await page.evaluate(key => JSON.parse(sessionStorage.getItem(key)!), `workbench:gem-publication:v1:${accountId}`);
  expect(retained).toEqual(request);
  // WHEN reloading after the response was lost THEN lookup restores success and clears the minimal receipt.
  await page.reload();
  await expect(page.getByRole('status').filter({ hasText: 'Published 1 shared entry.' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'View published entry 1', exact: true })).toHaveAttribute('href', `${library}/entries/${draft.entryId}`);
  expect(await page.evaluate(key => sessionStorage.getItem(key), `workbench:gem-publication:v1:${accountId}`)).toBeNull();
});
