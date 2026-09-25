import { expect, test } from './diagnostic-fixture';
import { useInterceptedSession } from './intercepted-auth-fixture';

for (const width of [320, 1440]) test(`supplier handles retain validation focus and responsive geometry at ${width}px`, async ({ page }) => {
  // GIVEN deterministic validation and collection responses owned before any navigation.
  await page.route('**/api/beta/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route('**/api/beta/suppliers', route => {
    expect(route.request().method()).toBe('POST');
    return route.fulfill({ status: 400, contentType: 'application/problem+json', json: {
      status: 400, title: 'Review the supplier fields.', code: 'supplier_validation_failed',
      errors: { 'supplier.socialProfiles[0].label': ['Enter a label.'] },
    } });
  });
  await useInterceptedSession(page);
  await page.setViewportSize({ width, height: 900 });
  await page.goto('/suppliers/new');
  await page.getByLabel('Supplier name', { exact: true }).fill(`Reference supplier ${width}`);
  await page.getByLabel('Website', { exact: true }).fill('https://example.test');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('URL / Handle', { exact: true }).nth(0).fill('@gem dealer');
  // WHEN saving an incomplete pair THEN the missing label receives actionable feedback and focus.
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByLabel('Platform', { exact: true }).nth(0)).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Platform', { exact: true }).nth(0)).toBeFocused();
  await expect(page.getByLabel('URL / Handle', { exact: true }).nth(0)).toHaveValue('@gem dealer');
  await page.getByLabel('Platform', { exact: true }).nth(0).fill('Trade chat');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Platform', { exact: true }).nth(1).fill('Mastodon');
  await page.getByLabel('URL / Handle', { exact: true }).nth(1).fill('@gems@stones.example');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Platform', { exact: true }).nth(2).fill('Gem forum');
  await page.getByLabel('URL / Handle', { exact: true }).nth(2).fill('https://example.test/member');
  const profiles = page.getByRole('group', { name: 'Social handles (optional)' });
  // THEN the unnumbered fields fill a desktop row with removal at the right; mobile stacks them.
  const platformBox = (await page.getByLabel('Platform', { exact: true }).first().boundingBox())!;
  const handleBox = (await page.getByLabel('URL / Handle', { exact: true }).first().boundingBox())!;
  const removeBox = (await page.getByRole('button', { name: 'Remove social 1', exact: true }).boundingBox())!;
  if (width > 700) {
    expect(Math.abs(platformBox.y - handleBox.y)).toBeLessThan(2);
    expect(Math.abs(platformBox.y - removeBox.y)).toBeLessThan(2);
    expect(removeBox.x).toBeGreaterThan(handleBox.x + handleBox.width);
  } else {
    expect(handleBox.y).toBeGreaterThan(platformBox.y + platformBox.height);
    expect(removeBox.y).toBeGreaterThan(handleBox.y + handleBox.height);
    expect(Math.abs(removeBox.x + removeBox.width - handleBox.x - handleBox.width)).toBeLessThan(2);
  }
  await expect(profiles.getByRole('link')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
