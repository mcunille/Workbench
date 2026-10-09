import { expect, test, type Page } from './diagnostic-fixture';
import { captureEvidence } from './evidence-fixture';
import { referenceFixture } from '../../src/Workbench.Client/src/features/gemReference/gemFixture';

async function identity(page: Page) {
  await page.route('**/api/beta/system', route => route.fulfill({ json: { name: 'Workbench', version: 'synthetic' } }));
  await page.route('**/api/beta/auth/me', route => route.fulfill({ json: { userId: 'synthetic', tenantName: 'Synthetic studio', email: 'synthetic@example.test', permissions: ['TenantAccess'] } }));
}
for (const theme of ['light', 'dark'] as const) test(`keyboard browsing, source anchors and narrow reading in ${theme}`, async ({ page }) => {
  // GIVEN a synthetic organic entry with aliases/group/locality and a long citation, in each appearance.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' });
  await identity(page);
  const fixture = { ...referenceFixture, materialKind: 'organic', species: null, group: 'Synthetic group', effectiveFields: { ...referenceFixture.effectiveFields, group: { state: 'inherit', attribution: 'workbench', sources: [] }, species: { state: 'inherit', attribution: 'workbench', sources: [] }, notableLocality: { ...referenceFixture.effectiveFields!.notableLocality, sources: [{ ...referenceFixture.effectiveFields!.notableLocality.sources[0], citation: 'SyntheticLongCitation'.repeat(100) }] } } };
  let received: URLSearchParams | undefined;
  await page.route('**/api/beta/gem-reference**', route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/beta/gem-reference') { received = url.searchParams; return route.fulfill({ json: { entries: [fixture], nextCursor: null } }); }
    expect(url.searchParams.get('origin')).toBe('workbench');
    return route.fulfill({ json: fixture });
  });
  await page.goto('/gem-reference');
  // WHEN using the keyboard to apply filters and open a result.
  const search = page.getByLabel('Search gems', { exact: true });
  await search.fill('Synthetic red gem');
  await page.getByLabel('Material kind', { exact: true }).selectOption('organic');
  await page.getByLabel('Group', { exact: true }).fill('Synthetic group');
  await search.focus();
  await page.keyboard.press('Enter');
  await expect.poll(() => received?.get('query')).toBe('Synthetic red gem');
  expect(received?.get('materialKind')).toBe('organic');
  expect(received?.get('group')).toBe('Synthetic group');
  const link = page.getByRole('link', { name: 'Synthetic ruby', exact: true });
  await link.focus();
  await page.keyboard.press('Enter');
  // THEN detail focus, source links and non-mineral/locality limits work without overflow.
  await expect(page.getByRole('heading', { name: 'Synthetic ruby', exact: true })).toBeFocused();
  await expect(page.getByText('Mineral species is not required for this material kind.')).toBeVisible();
  await expect(page.getByText('Synthetic red gem', { exact: true })).toBeVisible();
  await expect(page.getByText(/does not establish the origin of an individual specimen/)).toBeVisible();
  const locality = page.locator('dt').filter({ hasText: /^Notable locality$/ }).locator('..');
  await locality.getByRole('link', { name: 'Supporting sources' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.locator(':target')).toContainText('Synthetic locality report');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await captureEvidence(page, `gem-reference/detail-390-${theme}.png`);
  await page.setViewportSize({ width: 320, height: 700 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('link', { name: 'Back to gem reference' }).click();
  await expect(search).toHaveValue('Synthetic red gem');
  await expect(page.getByRole('link', { name: 'Synthetic ruby', exact: true })).toBeFocused();
  await page.setViewportSize({ width: 1280, height: 900 });
  await captureEvidence(page, `gem-reference/list-desktop-${theme}.png`, { fullPage: true });
});

test('retries failed loads and reads an invalid effective entry without a taxonomy claim', async ({ page }) => {
  // GIVEN a transient failure before an invalid entry becomes available.
  await identity(page);
  let first = true;
  const invalid = { ...referenceFixture, needsReview: true, reviewReasons: { species: ['A mineral requires a species.'] } };
  await page.route('**/api/beta/gem-reference**', route => {
    if (first) { first = false; return route.fulfill({ status: 503, json: { title: 'Unavailable' } }); }
    return route.fulfill({ json: new URL(route.request().url()).pathname === '/api/beta/gem-reference' ? { entries: [invalid], nextCursor: null } : invalid });
  });
  await page.goto('/gem-reference');
  // WHEN retrying THEN the invalid entry stays name-findable with an explicit warning.
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  const link = page.getByRole('link', { name: 'Synthetic ruby', exact: true });
  await expect(link.locator('..')).toContainText('Needs review');
  await expect(link.locator('..')).not.toContainText('Corundum');
  await link.click();
  await expect(page.getByRole('region', { name: 'Needs review', exact: true })).toContainText('A mineral requires a species.');
  await expect(page.locator('dt').filter({ hasText: /^Species$/ })).toHaveCount(0);
});
