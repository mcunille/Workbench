import { expect, test } from '@playwright/test';
import { useAuthenticatedSession } from './auth-fixture';
import { join } from 'node:path';

for (const width of [1280, 390]) {
  test(`user actions expand upward at ${width}px`, async ({ page }) => {
    // GIVEN a signed-in workspace with the personal actions collapsed.
    await page.setViewportSize({ width, height: 900 });
    await page.emulateMedia({ colorScheme: 'light' });
    await useAuthenticatedSession(page);
    const nav = page.getByRole('navigation', { name: 'Workspace' });
    const trigger = nav.getByRole('button', { name: 'User menu' });
    await expect(nav.getByRole('link', { name: 'Account', exact: true })).toHaveCount(0);
    await expect(page.getByRole('banner')).toHaveCount(0);
    await expect(nav.getByRole('link', { name: 'Administration' })).toHaveCount(0);
    await expect(nav.getByRole('switch', { name: 'Dark theme' })).toHaveCount(0);
    const brand = (await nav.getByRole('img', { name: 'Workbench' }).boundingBox())!;
    const inventory = (await nav.getByRole('link', { name: 'Inventory' }).boundingBox())!;
    expect(brand.y + brand.height).toBeLessThanOrEqual(inventory.y);
    // WHEN the system theme changes while the menu is closed THEN appearance still follows it.
    await page.emulateMedia({ colorScheme: 'dark' });
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await page.emulateMedia({ colorScheme: 'light' });
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    const before = (await trigger.boundingBox())!;
    // WHEN the row is opened with the keyboard.
    await trigger.focus();
    await page.keyboard.press('Enter');
    const account = nav.getByRole('link', { name: 'Account', exact: true });
    const signOut = nav.getByRole('button', { name: 'Sign out', exact: true });
    await expect(account).toBeVisible();
    await expect(signOut).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Administration' })).toBeVisible();
    const appearance = nav.getByRole('switch', { name: 'Dark theme' });
    await expect(appearance).toBeVisible();
    const switchBox = (await appearance.boundingBox())!;
    expect(switchBox.width).toBe(92);
    expect(switchBox.height).toBe(44);
    const tenant = (await nav.getByText('Browser Tenant', { exact: true }).boundingBox())!;
    const version = (await nav.getByText(/^Workbench \d/).boundingBox())!;
    expect(tenant.y + tenant.height).toBeLessThanOrEqual(version.y);
    // THEN actions sit above the trigger, remain in the viewport, and do not overflow.
    const after = (await trigger.boundingBox())!;
    const actions = (await signOut.boundingBox())!;
    expect(actions.y + actions.height).toBeLessThanOrEqual(after.y);
    if (width > 768) expect(after.y).toBeCloseTo(before.y, 0);
    expect(after.y + after.height).toBeLessThanOrEqual(900);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    const evidenceDirectory = process.env.WORKBENCH_MENU_EVIDENCE_DIRECTORY;
    if (evidenceDirectory) await page.screenshot({ path: join(evidenceDirectory, `user-menu-${width}.png`), fullPage: true });
    // AND an explicit choice persists after the menu closes and the system changes.
    await appearance.setChecked(true);
    // WHEN dismissing from an action THEN focus returns to the collapsed trigger.
    await signOut.focus();
    await page.keyboard.press('Escape');
    await expect(trigger).toBeFocused();
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await expect(signOut).toHaveCount(0);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.emulateMedia({ colorScheme: 'light' });
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  });
}
