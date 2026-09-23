import { expect, test } from './diagnostic-fixture';
import { useInterceptedSession } from './intercepted-auth-fixture';
import { captureEvidence } from './evidence-fixture';

for (const width of [1440, 320]) test(`accounting pending navigation and conflict comparison at ${width}px`, async ({ page }) => {
  // GIVEN synthetic accounting setup and an account with a concurrent description change
  await page.setViewportSize({ width, height: 1000 });
  await page.route('**/api/beta/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await useInterceptedSession(page);
  await page.route('**/api/beta/auth/me', route => route.fulfill({ json: { userId: 'sample', tenantName: 'Sample Studio', email: 'preview@example.test', permissions: ['TenantAccess', 'AccountingConfigurationRead', 'AccountingConfigurationManage'] } }));
  const configuration = { policies: { country: null, region: null, currency: null, scale: null, fiscalStartMonth: null, startApproach: null, plannedStartDate: null, retentionYears: null, retentionRationale: null, frameworkNotes: null }, mappings: [], coverage: [] };
  await page.route('**/api/beta/accounting/catalog', route => route.fulfill({ json: { version: '1', countries: [], currencies: [], accountTypes: ['Asset'], accountPurposes: ['General'], mappingSlots: [], starterAccounts: [] } }));
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/beta/accounting/setup', async route => {
    if (route.request().method() === 'PUT') { await pending; await route.fulfill({ json: { savedVersion: 'v2', accountIds: [] } }); }
    else await route.fulfill({ json: { configuration, version: 'v1', setupComplete: false, bookkeepingAvailable: false, missingItems: [], blockers: ['Bookkeeping is not yet available.'] } });
  });
  const account = { id: 'bank', code: '1000', name: 'Bank', type: 'Asset', purpose: 'General', description: 'Original description', isArchived: false, version: 'v1' };
  let conflict = false;
  await page.route('**/api/beta/accounting/accounts?**', route => route.fulfill({ json: { items: [conflict ? { ...account, description: 'Current saved description from another administrator', version: 'v2' } : account], nextCursor: null } }));
  await page.route('**/api/beta/accounting/accounts', route => route.fulfill({ json: { items: [conflict ? { ...account, description: 'Current saved description from another administrator', version: 'v2' } : account], nextCursor: null } }));
  await page.route('**/api/beta/accounting/accounts/bank', route => { conflict = true; return route.fulfill({ status: 409, json: { detail: 'The account changed.' } }); });
  await page.goto('/accounting');
  // WHEN navigating while a save is pending THEN warn that leaving cannot undo the command
  await page.getByRole('button', { name: 'Save setup', exact: true }).click();
  try {
    await page.getByRole('link', { name: 'Inventory', exact: true }).click();
    await expect(page.getByRole('dialog')).toContainText('Leaving cannot undo it and loses the retry request kept in memory.');
    await page.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page).toHaveURL(/\/accounting$/);
  } finally { release(); }
  await expect(page.getByText('Accounting setup saved. Bookkeeping is not yet available.')).toBeVisible();
  // WHEN reloading a conflicted account THEN compare saved values without discarding the draft
  await page.getByRole('button', { name: 'Accounts and mappings', exact: true }).click();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByLabel('Description (optional)', { exact: true }).fill('My retained draft description');
  await page.getByRole('button', { name: 'Save account', exact: true }).click();
  await page.getByRole('button', { name: 'Reload accounts and retain draft' }).click();
  await expect(page.getByRole('region', { name: 'Current saved account' })).toContainText('Current saved description from another administrator');
  await expect(page.getByLabel('Description (optional)', { exact: true })).toHaveValue('My retained draft description');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await captureEvidence(page, `accounting-feedback-${width}.png`);
});
