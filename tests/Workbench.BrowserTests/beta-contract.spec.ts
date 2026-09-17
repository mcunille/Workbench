import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';

test.setTimeout(120_000);

test('an incompatible beta API preserves purchase edits and blocks subsequent writes', async ({ page }) => {
  // GIVEN an authenticated owner editing a purchase draft while the deployed API changes.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill('Retain my purchase draft');
  await page.getByLabel('Notes', { exact: true }).fill('Ask the supplier about the blue stones.');
  let saves = 0;
  await page.route('**/api/beta/purchase-order-drafts', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    saves++;
    // The application itself supplies the revision; the browser context does not inject one.
    expect(route.request().headers()['x-workbench-api-revision']).toBe('beta-1');
    await route.fulfill({ status: 409, contentType: 'application/problem+json', body: JSON.stringify({
      title: 'API contract unsupported', code: 'api_contract_unsupported',
    }) });
  });

  // WHEN the owner's save receives the incompatible-contract response.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();

  // THEN the notice explains recovery and the same unsaved form remains on screen.
  await expect(page.getByText('Workbench has been updated. Reload required.', { exact: true })).toBeVisible();
  await expect(page.getByText('Saving is paused. Your unsaved changes are still on this page. Copy them before reloading.', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Title', { exact: true })).toHaveValue('Retain my purchase draft');
  await expect(page.getByLabel('Notes', { exact: true })).toHaveValue('Ask the supplier about the blue stones.');
  await expect(page).toHaveURL(/\/purchase-orders\/new$/);

  // WHEN retry is requested THEN the client blocks all further unsafe API network requests.
  const subsequentWrites: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/beta/') && !['GET', 'HEAD', 'OPTIONS'].includes(request.method())) {
      subsequentWrites.push(request.method());
    }
  });
  await page.getByRole('button', { name: 'Check and retry', exact: true }).click();
  await expect(page.getByText('We couldn’t confirm your save.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Check and retry', exact: true })).toBeEnabled();
  expect(subsequentWrites).toEqual([]);
  expect(saves).toBe(1);
  await expect(page.getByLabel('Notes', { exact: true })).toHaveValue('Ask the supplier about the blue stones.');
});
