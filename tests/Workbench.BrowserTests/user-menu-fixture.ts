import { type Page } from '@playwright/test';

export async function openUserMenu(page: Page) {
  const trigger = page.getByRole('button', { name: 'User menu', exact: true });
  if (await trigger.getAttribute('aria-expanded') === 'false') await trigger.click();
}

export async function setAppearance(page: Page, dark: boolean) {
  const trigger = page.getByRole('button', { name: 'User menu', exact: true });
  const wasClosed = await trigger.count() > 0 && await trigger.getAttribute('aria-expanded') === 'false';
  if (wasClosed) await trigger.click();
  await page.getByRole('switch', { name: 'Dark theme', exact: true }).setChecked(dark);
  if (wasClosed) await trigger.click();
}
