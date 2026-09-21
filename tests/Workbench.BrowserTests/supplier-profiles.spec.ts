import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession as signIn } from './auth-fixture';

for (const width of [320, 1440]) test(`custom supplier handles create, validate, edit and remove at ${width}px`, async ({ page }) => {
  // GIVEN a new supplier and a user-defined handle with no label yet.
  await signIn(page);
  await page.setViewportSize({ width, height: 900 });
  await page.goto('/suppliers/new');
  await page.getByLabel('Supplier name', { exact: true }).fill(`Reference supplier ${width}`);
  await page.getByLabel('Website', { exact: true }).fill('https://example.test');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Handle 1', { exact: true }).fill('@gem dealer');
  // WHEN saving an incomplete pair THEN the missing label receives actionable feedback and focus.
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByLabel('Label 1', { exact: true })).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Label 1', { exact: true })).toBeFocused();
  await page.getByLabel('Label 1', { exact: true }).fill('Trade chat');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Label 2', { exact: true }).fill('Mastodon');
  await page.getByLabel('Handle 2', { exact: true }).fill('@gems@stones.example');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Label 3', { exact: true }).fill('Gem forum');
  await page.getByLabel('Handle 3', { exact: true }).fill('https://example.test/member');
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page).toHaveURL(/\/suppliers\/[a-f0-9-]{36}$/);
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  await page.reload();
  // THEN arbitrary labels and handles persist as reference text, without links or overflow.
  await expect(page.getByLabel('Handle 1', { exact: true })).toHaveValue('@gem dealer');
  await expect(page.getByLabel('Handle 2', { exact: true })).toHaveValue('@gems@stones.example');
  const profiles = page.getByRole('group', { name: 'Social handles (optional)' });
  await expect(profiles.getByRole('link')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  // WHEN renaming, editing and removing a pair THEN reload preserves the requested list and the separate website.
  await page.getByLabel('Label 1', { exact: true }).fill('Discord');
  await page.getByLabel('Handle 1', { exact: true }).fill('gemdealer');
  await page.getByRole('button', { name: 'Remove social 2', exact: true }).click();
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  await page.reload();
  await expect(page.getByLabel('Label 1', { exact: true })).toHaveValue('Discord');
  await expect(page.getByLabel('Handle 1', { exact: true })).toHaveValue('gemdealer');
  await expect(page.getByLabel('Label 2', { exact: true })).toHaveValue('Gem forum');
  await expect(page.getByLabel('Handle 2', { exact: true })).toHaveValue('https://example.test/member');
  await expect(page.getByLabel('Handle 3', { exact: true })).toHaveCount(0);
  await expect(page.getByLabel('Website', { exact: true })).toHaveValue('https://example.test');
});
