import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';

test('forced colors preserve both icons and a visible selected-theme boundary', async ({ page }) => {
  // GIVEN a visitor using a high-contrast system palette.
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await page.goto('/');
  const control = page.getByRole('switch', { name: 'Dark theme' });
  await expect(control).toBeVisible();
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });
    for (const dark of [false, true]) {
      // WHEN choosing either theme in each high-contrast palette.
      await control.setChecked(dark);
      const paint = await control.evaluate(element => {
        const thumb = getComputedStyle(element, '::before');
        return {
          background: getComputedStyle(element).backgroundColor,
          thumbBackground: thumb.backgroundColor,
          borderColor: thumb.borderTopColor,
          borderStyle: thumb.borderTopStyle,
          borderWidth: parseFloat(thumb.borderTopWidth),
          icons: [...element.querySelectorAll('svg')].map(icon => getComputedStyle(icon).color),
        };
      });
      // THEN the selected position has a contrasting boundary and both icons remain visible.
      expect(paint.borderStyle).not.toBe('none');
      expect(paint.borderWidth).toBeGreaterThanOrEqual(1);
      expect(paint.borderColor).not.toBe(paint.background);
      expect(paint.icons[dark ? 1 : 0]).not.toBe(paint.thumbBackground);
      expect(paint.icons[dark ? 0 : 1]).not.toBe(paint.background);
      await mkdir('../../artifacts/theme-switch', { recursive: true });
      await control.screenshot({ path: `../../artifacts/theme-switch/forced-${colorScheme}-${dark ? 'dark' : 'light'}.png` });
    }
  }
});

for (const width of [320, 1280]) {
  test(`theme switch follows system until chosen and persists at ${width}px`, async ({ page }) => {
    // GIVEN a new visitor with dark system appearance and no saved choice.
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: 'dark', reducedMotion: 'reduce' });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
    const control = page.getByRole('switch', { name: 'Dark theme' });
    await expect(control).toBeChecked();
    await expect(page.getByRole('combobox', { name: 'Appearance' })).toHaveCount(0);
    await page.getByLabel('Email', { exact: true }).fill('collector@example.test');
    // WHEN the system changes before the first toggle.
    await page.emulateMedia({ colorScheme: 'light' });
    // THEN the control and page follow without saving a preference.
    await expect(control).not.toBeChecked();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    expect(await page.evaluate(() => localStorage.getItem('workbench.appearance'))).toBeNull();
    await mkdir('../../artifacts/theme-switch', { recursive: true });
    await page.screenshot({ path: `../../artifacts/theme-switch/sign-in-${width}-light.png`, fullPage: true });
    // WHEN the user activates the switch with the keyboard.
    await control.focus();
    await page.keyboard.press('Space');
    // THEN the choice changes, focus and form input remain, and the target fits the viewport.
    await expect(control).toBeChecked();
    await expect(control).toBeFocused();
    await expect(page.getByLabel('Email', { exact: true })).toHaveValue('collector@example.test');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    const bounds = await control.boundingBox();
    expect(bounds!.height).toBeGreaterThanOrEqual(44);
    expect(bounds!.width).toBeGreaterThanOrEqual(44);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `../../artifacts/theme-switch/sign-in-${width}-dark.png`, fullPage: true });
    // WHEN reloading and changing the system after an explicit choice.
    await page.reload();
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.emulateMedia({ colorScheme: 'light' });
    // THEN the saved choice overrides the system, and Enter toggles back to light.
    await expect(control).toBeChecked();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await control.focus();
    await page.keyboard.press('Enter');
    await expect(control).not.toBeChecked();
    expect(await page.evaluate(() => localStorage.getItem('workbench.appearance'))).toBe('light');
    // AND enlarged text does not push the icons outside the fixed switch target.
    await page.addStyleTag({ content: 'html { font-size: 200%; }' });
    for (const icon of await control.locator('svg').all()) {
      const iconBounds = await icon.boundingBox();
      expect(iconBounds!.width).toBe(18);
      expect(iconBounds!.height).toBe(18);
    }
  });
}
