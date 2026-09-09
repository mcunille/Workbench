import { type Page } from '@playwright/test';

export async function openUserMenu(page: Page) {
  const trigger = page.getByRole('button', { name: 'User menu', exact: true });
  if (await trigger.getAttribute('aria-expanded') === 'false') await trigger.click();
}

export async function setAppearance(page: Page, dark: boolean) {
  const trigger = page.getByRole('button', { name: 'User menu', exact: true });
  const wasClosed = await trigger.count() > 0 && await trigger.getAttribute('aria-expanded') === 'false';
  if (wasClosed) await trigger.click();
  const appearance = page.getByRole('button', { name: /^Appearance / });
  if (await appearance.count()) {
    const expected = `Appearance ${dark ? 'Dark' : 'Light'}`;
    for (let attempt = 0; attempt < 3 && await appearance.getAttribute('aria-label') !== expected; attempt++) {
      await appearance.click();
    }
    if (await appearance.getAttribute('aria-label') !== expected) throw new Error('Appearance did not reach requested theme.');
  } else {
    await page.getByRole('switch', { name: 'Dark theme', exact: true }).setChecked(dark);
  }
  if (wasClosed) await trigger.click();
}
