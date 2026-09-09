import { type Page } from '@playwright/test';

export async function openUserMenu(page: Page) {
  const trigger = page.getByRole('button', { name: 'User menu', exact: true });
  if (await trigger.getAttribute('aria-expanded') === 'false') await trigger.click();
}
