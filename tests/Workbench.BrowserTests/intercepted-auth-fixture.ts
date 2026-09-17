import { expect, type Page } from './diagnostic-fixture';

export async function useInterceptedSession(page: Page) {
  await page.route('**/api/beta/system', route => route.fulfill({ json: { name: 'Workbench', version: 'test' } }));
  await page.route('**/api/beta/auth/me', route => route.fulfill({ json: {
    userId: 'sample', tenantName: 'Sample Studio', email: 'preview@example.test',
    permissions: ['TenantAccess', 'TenantUsersManage'],
  } }));
  await page.route('**/api/beta/auth/antiforgery', route => route.fulfill({ json: { requestToken: 'synthetic-csrf' } }));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Collection', exact: true })).toBeVisible();
}
