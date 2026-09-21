import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession as signIn } from './auth-fixture';

for (const width of [320, 1440]) test(`supplier profiles create, validate, edit and remove at ${width}px`, async ({ page }) => {
  // GIVEN a new supplier with independent website and platform links.
  await signIn(page);
  await page.setViewportSize({ width, height: 900 });
  await page.goto('/suppliers/new');
  await page.getByLabel('Supplier name', { exact: true }).fill(`Profile supplier ${width}`);
  await page.getByLabel('Website', { exact: true }).fill('https://example.test');
  await page.getByLabel('Instagram', { exact: true }).fill('javascript:alert(1)');
  await page.getByLabel('X', { exact: true }).fill('https://x.com/example');
  await page.getByLabel('GemRockAuctions', { exact: true }).fill('https://www.gemrockauctions.com/stores/example');
  // WHEN saving unsafe input THEN the platform receives focused actionable feedback.
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByLabel('Instagram', { exact: true })).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Instagram', { exact: true })).toBeFocused();
  await page.getByLabel('Instagram', { exact: true }).fill('https://www.instagram.com/example/');
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page).toHaveURL(/\/suppliers\/[a-f0-9-]{36}$/);
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  await page.reload();
  // THEN each saved platform has a safe link and mobile layout remains contained.
  for (const label of ['Instagram', 'X', 'GemRockAuctions']) {
    const link = page.getByRole('link', { name: `Open ${label} profile (new tab)`, exact: true });
    await expect(link).toHaveAttribute('target', '_blank');
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  // WHEN changing and removing profiles THEN reload preserves only the requested destinations.
  await page.getByLabel('Instagram', { exact: true }).fill('https://instagram.com/changed');
  await page.getByLabel('X', { exact: true }).fill('');
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
  await page.reload();
  await expect(page.getByLabel('Instagram', { exact: true })).toHaveValue('https://instagram.com/changed');
  await expect(page.getByLabel('X', { exact: true })).toHaveValue('');
  await expect(page.getByRole('link', { name: 'Open X profile (new tab)', exact: true })).toHaveCount(0);
  await expect(page.getByLabel('Website', { exact: true })).toHaveValue('https://example.test');
  await expect(page.getByLabel('GemRockAuctions', { exact: true })).toHaveValue('https://www.gemrockauctions.com/stores/example');
});
