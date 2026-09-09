import { expect, test } from '@playwright/test';
import { useAuthenticatedSession as signIn } from './auth-fixture';

test('the empty collection card opens item creation by pointer and keyboard', async ({ page }) => {
  // GIVEN an authenticated collection with an empty API response.
  await page.route('**/api/items', route => route.fulfill({
    json: { items: [], nextCursor: null },
  }));
  await signIn(page);
  const card = page.getByRole('link', { name: /Your collection starts here/ });
  // WHEN clicking the card's padding on a narrow screen.
  await page.setViewportSize({ width: 320, height: 900 });
  await expect(card).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await card.click({ position: { x: 12, y: 12 } });
  // THEN the existing add-item flow opens.
  await expect(page).toHaveURL(/\/inventory\/new$/);
  await expect(page.getByRole('button', { name: 'Save item', exact: true })).toBeVisible();
  // WHEN returning and reaching the card using the keyboard.
  await page.goto('/inventory');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.getByRole('button', { name: 'Clear', exact: true }).focus();
  await page.keyboard.press('Tab');
  // THEN it has visible keyboard focus and Enter opens the same flow.
  await expect(card).toBeFocused();
  expect(await card.evaluate(element => getComputedStyle(element).outlineStyle)).toBe('solid');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/inventory\/new$/);
  await expect(page.getByRole('button', { name: 'Save item', exact: true })).toBeVisible();
});
