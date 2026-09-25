import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession as signIn } from './auth-fixture';

test('custom supplier handles persist through create, validation, edit, removal and reload', async ({ page }) => {
  // GIVEN a new supplier and a user-defined handle with no label yet.
  await signIn(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/suppliers/new');
  await page.getByLabel('Supplier name', { exact: true }).fill('Reference supplier');
  await page.getByLabel('Website', { exact: true }).fill('https://example.test');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('URL / Handle', { exact: true }).nth(0).fill('@gem dealer');
  // WHEN saving an incomplete pair THEN the missing label receives actionable feedback and focus.
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByLabel('Platform', { exact: true }).nth(0)).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Platform', { exact: true }).nth(0)).toBeFocused();
  await page.getByLabel('Platform', { exact: true }).nth(0).fill('Trade chat');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Platform', { exact: true }).nth(1).fill('Mastodon');
  await page.getByLabel('URL / Handle', { exact: true }).nth(1).fill('@gems@stones.example');
  await page.getByRole('button', { name: 'Add social', exact: true }).click();
  await page.getByLabel('Platform', { exact: true }).nth(2).fill('Gem forum');
  await page.getByLabel('URL / Handle', { exact: true }).nth(2).fill('https://example.test/member');
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page).toHaveURL(/\/suppliers\/[a-f0-9-]{36}$/);
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  await page.reload();
  // THEN arbitrary labels and handles persist as reference text, without links or overflow.
  await expect(page.getByLabel('URL / Handle', { exact: true }).nth(0)).toHaveValue('@gem dealer');
  await expect(page.getByLabel('URL / Handle', { exact: true }).nth(1)).toHaveValue('@gems@stones.example');
  const profiles = page.getByRole('group', { name: 'Social handles (optional)' });
  await expect(profiles.getByRole('link')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  // WHEN renaming, editing and removing a pair THEN reload preserves the requested list and the separate website.
  await page.getByLabel('Platform', { exact: true }).nth(0).fill('Discord');
  await page.getByLabel('URL / Handle', { exact: true }).nth(0).fill('gemdealer');
  await page.getByRole('button', { name: 'Remove social 2', exact: true }).click();
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  await page.reload();
  await expect(page.getByLabel('Platform', { exact: true }).nth(0)).toHaveValue('Discord');
  await expect(page.getByLabel('URL / Handle', { exact: true }).nth(0)).toHaveValue('gemdealer');
  await expect(page.getByLabel('Platform', { exact: true }).nth(1)).toHaveValue('Gem forum');
  await expect(page.getByLabel('URL / Handle', { exact: true }).nth(1)).toHaveValue('https://example.test/member');
  await expect(page.getByLabel('URL / Handle', { exact: true }).nth(2)).toHaveCount(0);
  await expect(page.getByLabel('Website', { exact: true })).toHaveValue('https://example.test');
});
