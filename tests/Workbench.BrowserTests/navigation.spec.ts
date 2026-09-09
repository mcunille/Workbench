import { expect, test } from '@playwright/test';
import { useAuthenticatedSession } from './auth-fixture';
import { setAppearance } from './user-menu-fixture';
import { join } from 'node:path';

test('focused skip link stays above the glass pane', async ({ page }) => {
  // GIVEN a desktop workspace with a glass pane.
  await page.setViewportSize({ width: 1280, height: 900 });
  await useAuthenticatedSession(page);
  // WHEN the keyboard skip link receives focus THEN the pane cannot cover it.
  const skip = page.getByRole('link', { name: 'Skip to content' });
  await skip.focus();
  expect(await skip.evaluate(el => {
    const box = el.getBoundingClientRect();
    return el.contains(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2));
  })).toBe(true);
});

test('cancelling profile sign-out restores usable keyboard focus', async ({ page }) => {
  // GIVEN an unsaved item draft and an open profile disclosure.
  await useAuthenticatedSession(page);
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill('Unsaved navigation check');
  await page.getByRole('button', { name: 'User menu' }).click();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  // WHEN keeping the draft THEN focus returns to a visible profile control.
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeFocused();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue('Unsaved navigation check');
});

test('desktop pane preserves icon positions and keyboard access through collapse', async ({ page }) => {
  // GIVEN an expanded authenticated workspace with ordinary motion enabled.
  await page.setViewportSize({ width: 1280, height: 900 });
  await useAuthenticatedSession(page);
  const nav = page.getByRole('navigation', { name: 'Workspace' });
  const inventory = nav.getByRole('link', { name: 'Inventory' });
  const icon = inventory.locator('svg');
  const initial = (await icon.boundingBox())!;
  const toggle = nav.getByRole('button', { name: 'Collapse navigation' });
  const toggleBefore = (await toggle.boundingBox())!;
  // WHEN the pane collapses THEN icons stay on their column throughout the animation.
  const samples = await page.evaluate(async () => {
    const button = document.querySelector<HTMLButtonElement>('.navigation-toggle')!;
    const icon = document.querySelector('.navigation-destination .icon')!;
    button.click();
    const frames: { x: number; y: number }[] = [];
    const start = performance.now();
    while (performance.now() - start < 320) {
      await new Promise(requestAnimationFrame);
      const box = icon.getBoundingClientRect();
      frames.push({ x: box.x, y: box.y });
    }
    return frames;
  });
  expect(samples.length).toBeGreaterThan(1);
  for (const sample of samples) {
    expect(sample.x).toBeCloseTo(initial.x, 0);
    expect(sample.y).toBeCloseTo(initial.y, 0);
  }
  const expand = nav.getByRole('button', { name: 'Expand navigation' });
  const toggleAfter = (await expand.boundingBox())!;
  expect(toggleAfter.y).toBeCloseTo(toggleBefore.y, 0);
  expect(toggleAfter.x + toggleAfter.width / 2).toBeCloseTo(initial.x + initial.width / 2, 0);
  // AND the account panel stays above content and Escape restores focus.
  await nav.getByRole('button', { name: 'User menu' }).click();
  await nav.getByRole('link', { name: 'Account', exact: true }).focus();
  await page.keyboard.press('Escape');
  await expect(nav.getByRole('button', { name: 'User menu' })).toBeFocused();
  // WHEN reduced motion is requested THEN the pane switches without animation.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await expand.click();
  expect(await page.locator('.workspace-layout').evaluate(el => getComputedStyle(el).transitionDuration)).toBe('0s');
  const directory = process.env.WORKBENCH_MENU_EVIDENCE_DIRECTORY;
  if (directory) await page.screenshot({ path: join(directory, 'navigation-desktop.png') });
});

for (const width of [320, 390]) {
  test(`mobile pill leaves content reachable at ${width}px`, async ({ page }) => {
    // GIVEN real authenticated collection content and a narrow viewport.
    await page.setViewportSize({ width, height: 844 });
    await useAuthenticatedSession(page);
    const name = `Navigation clearance ${width}`;
    await page.getByRole('link', { name: 'Add item', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill(name);
    await page.getByRole('button', { name: 'Save item', exact: true }).click();
    await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
    await expect(page.getByText(name, { exact: true })).toBeVisible();
    const nav = page.getByRole('navigation', { name: 'Workspace' });
    for (const dark of [false, true]) {
      await setAppearance(page, dark);
      // WHEN reaching the bottom THEN the content area ends above the pill.
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
      const geometry = await page.evaluate(() => {
        const main = document.querySelector('main')!;
        const bottom = main.getBoundingClientRect().bottom - parseFloat(getComputedStyle(main).paddingBottom);
        const nav = document.querySelector('.workspace-nav')!.getBoundingClientRect();
        return { bottom, pillTop: nav.top, right: nav.right, left: nav.left, overflow: document.documentElement.scrollWidth > innerWidth };
      });
      expect(geometry.bottom).toBeLessThan(geometry.pillTop);
      expect(geometry.left).toBeGreaterThanOrEqual(0);
      expect(geometry.right).toBeLessThanOrEqual(width);
      expect(geometry.overflow).toBe(false);
      const lastItem = (await page.locator('.collection-list li').last().boundingBox())!;
      expect(lastItem.y + lastItem.height).toBeLessThan(geometry.pillTop);
      // AND profile expands above the pill without moving the page content.
      const before = await page.locator('main').boundingBox();
      await nav.getByRole('button', { name: 'User menu' }).click();
      const panel = (await page.locator('#user-actions').boundingBox())!;
      expect(panel.x).toBeGreaterThanOrEqual(0);
      expect(panel.x + panel.width).toBeLessThanOrEqual(width);
      expect(panel.y).toBeGreaterThanOrEqual(0);
      expect(panel.y + panel.height).toBeLessThan(geometry.pillTop);
      expect(await page.locator('main').boundingBox()).toEqual(before);
      const directory = process.env.WORKBENCH_MENU_EVIDENCE_DIRECTORY;
      if (directory) await page.screenshot({ path: join(directory, `navigation-mobile-${width}-${dark ? 'dark' : 'light'}.png`) });
      await page.keyboard.press('Escape');
    }
    // AND a short phone screen keeps the upward profile usable through internal scrolling.
    await page.setViewportSize({ width, height: 480 });
    await nav.getByRole('button', { name: 'User menu' }).click();
    const shortPanel = (await page.locator('#user-actions').boundingBox())!;
    expect(shortPanel.y).toBeGreaterThanOrEqual(0);
    await nav.getByRole('button', { name: 'Sign out', exact: true }).scrollIntoViewIfNeeded();
    await expect(nav.getByRole('button', { name: 'Sign out', exact: true })).toBeInViewport();
  });
}
