import { randomUUID } from 'node:crypto';
import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { setAppearance } from './user-menu-fixture';

test.setTimeout(150_000);

test('accounting setup preserves explicit policies, guarded mappings, coverage, and role separation', async ({ page }) => {
  // GIVEN a tenant administrator without automatically granted accounting authority
  await useAuthenticatedSession(page);
  const identity = await (await page.request.get('/api/beta/auth/me')).json();
  const roles = await (await page.request.get('/api/beta/tenant/accounting-roles')).json();
  const original = await (await page.request.get(`/api/beta/tenant/users/${identity.userId}/accounting-roles`)).json();
  const csrf = await (await page.request.get('/api/beta/auth/antiforgery')).json();
  const rolePath = `/api/beta/tenant/users/${identity.userId}/accounting-roles`;
  async function assign(roleIds: string[]) {
    const current = await (await page.request.get(rolePath)).json();
    const response = await page.request.post(rolePath, { headers: { 'X-CSRF-TOKEN': csrf.requestToken }, data: { requestId: randomUUID(), expectedVersion: current.version, roleIds } });
    expect(response.ok()).toBe(true);
  }
  try {
    await page.getByRole('link', { name: 'Administration', exact: true }).click();
    await page.locator('.record-list li').filter({ hasText: identity.email }).getByRole('button', { name: 'Accounting roles' }).click();
    await page.getByLabel('Accounting administrator', { exact: true }).check();
    await expect(page.getByText(/Proposed grants:/)).toContainText('Accounting administrator');
    await page.getByRole('button', { name: 'Save accounting roles' }).click();
    await expect(page.getByText('Accounting roles saved. Changes take effect on the next request.')).toBeVisible();
    await page.getByRole('link', { name: 'Accounting', exact: true }).click();

    // WHEN explicit policies are saved THEN reload retains choices without activating bookkeeping
    await page.getByRole('combobox', { name: 'Country', exact: true }).selectOption('US');
    await page.getByRole('combobox', { name: 'State or region', exact: true }).selectOption('WA');
    await page.getByRole('combobox', { name: 'Functional currency', exact: true }).selectOption('USD');
    await expect(page.getByRole('combobox', { name: 'Posting decimal places', exact: true })).toHaveValue('');
    await page.getByRole('combobox', { name: 'Posting decimal places', exact: true }).selectOption('2');
    await page.getByRole('combobox', { name: 'Fiscal year starts', exact: true }).selectOption('1');
    await page.getByRole('combobox', { name: 'Starting approach', exact: true }).selectOption('FromBeginning');
    await page.getByLabel('Planned start date', { exact: true }).fill('2026-01-01');
    await page.getByLabel('Proposed document retention (years)', { exact: true }).fill('7');
    await page.getByLabel('Retention rationale or reference', { exact: true }).fill('Synthetic retention proposal for workflow verification.');
    await page.getByRole('button', { name: 'Save setup', exact: true }).click();
    await expect(page.getByText('Accounting setup saved. Bookkeeping is not yet available.')).toBeVisible();
    await page.reload();
    await expect(page.getByRole('combobox', { name: 'Functional currency', exact: true })).toHaveValue('USD');
    await expect(page.getByRole('combobox', { name: 'Posting decimal places', exact: true })).toHaveValue('2');

    // WHEN the optional starter chart is explicitly confirmed THEN typed controls become selectable
    await page.getByRole('button', { name: 'Accounts and mappings', exact: true }).click();
    await page.getByRole('button', { name: 'Preview starter chart', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Review starter chart' })).toBeVisible();
    await expect(page.getByText('1000 — Bank', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Create these accounts', exact: true }).click();
    await expect(page.getByText('Accounts saved.', { exact: true })).toBeVisible();
    await page.getByRole('combobox', { name: 'Supplier Payable', exact: true }).selectOption({ label: '2000 — Supplier payables' });
    await page.getByRole('button', { name: 'Save setup', exact: true }).click();
    await expect(page.getByText('Accounting setup saved. Bookkeeping is not yet available.')).toBeVisible();
    // THEN an actively mapped control cannot be archived
    const payable = page.locator('.record-list li').filter({ hasText: '2000 — Supplier payables' });
    const archiveResponse = page.waitForResponse(response => response.url().includes('/archive') && response.request().method() === 'POST');
    await payable.getByRole('button', { name: 'Archive', exact: true }).click();
    expect((await archiveResponse).status()).toBe(409);
    await page.getByRole('button', { name: 'Reload accounts and retain draft' }).click();
    await expect(page.getByText('Current accounts loaded. Review your retained draft before saving.')).toBeVisible();

    // AND an unused general account can be edited, archived, and restored with the same identity
    const equipment = page.locator('.record-list li').filter({ hasText: '1400 — Equipment' });
    await equipment.getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('combobox', { name: 'Account type', exact: true })).toBeDisabled();
    await expect(page.getByRole('combobox', { name: 'Account purpose', exact: true })).toBeDisabled();
    await page.getByLabel('Account name', { exact: true }).fill('Equipment and tools');
    await page.getByRole('button', { name: 'Save account', exact: true }).click();
    await expect(page.getByText('Accounts saved.', { exact: true })).toBeVisible();
    const equipmentArchive = page.waitForResponse(response => response.url().includes('/archive') && response.request().method() === 'POST');
    await equipment.getByRole('button', { name: 'Archive', exact: true }).click();
    const archiveUrl = (await equipmentArchive).url();
    await expect(equipment).toHaveCount(0);
    await page.getByLabel('Include archived', { exact: true }).check();
    await expect(equipment.getByText('Asset · General · Archived')).toBeVisible();
    const equipmentRestore = page.waitForResponse(response => response.url() === archiveUrl && response.request().method() === 'POST');
    await equipment.getByRole('button', { name: 'Restore', exact: true }).click();
    expect((await equipmentRestore).ok()).toBe(true);
    await expect(equipment.getByText('Asset · General', { exact: true })).toBeVisible();
    // WHEN recording no prior activity THEN the inventory retains unsupported future activity
    await page.getByRole('button', { name: 'Transaction coverage', exact: true }).click();
    const bank = page.getByRole('group', { name: '1000 — Bank', exact: true });
    await bank.getByRole('combobox', { name: 'Accounting perimeter', exact: true }).selectOption('include');
    await bank.getByRole('combobox', { name: 'Evidence basis', exact: true }).selectOption('NoPriorActivity');
    await bank.getByLabel('As-of date', { exact: true }).fill('2026-01-01');
    await bank.getByLabel('Rationale and recurring activity', { exact: true }).fill('New synthetic bank account; expect monthly fees.');
    await bank.getByRole('button', { name: 'Add transaction class', exact: true }).click();
    await bank.getByLabel('Class and expected activity', { exact: true }).fill('Monthly bank fees');
    await bank.getByLabel('Unresolved prerequisite', { exact: true }).fill('Bank fee accounting adapter');
    await bank.getByLabel('I have inventoried all statement and expected recurring activity.').check();
    await expect(bank.getByText('Transaction class 1 · Unsupported')).toBeVisible();
    await page.getByRole('button', { name: 'Save setup', exact: true }).click();
    await expect(page.getByText('Accounting setup saved. Bookkeeping is not yet available.')).toBeVisible();

    // WHEN another administrator changes setup THEN the stale draft survives for explicit reconciliation
    await page.getByRole('button', { name: 'Policies', exact: true }).click();
    await page.getByLabel('Framework and tax policy notes (optional)', { exact: true }).fill('Retained local draft');
    const latest = await (await page.request.get('/api/beta/accounting/setup')).json();
    latest.configuration.policies.frameworkNotes = 'Concurrent synthetic edit';
    const concurrent = await page.request.put('/api/beta/accounting/setup', { headers: { 'X-CSRF-TOKEN': csrf.requestToken }, data: { requestId: randomUUID(), expectedVersion: latest.version, configuration: latest.configuration } });
    expect(concurrent.ok()).toBe(true);
    await page.getByRole('button', { name: 'Save setup', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Resolve concurrent changes' })).toBeVisible();
    await expect(page.getByLabel('Framework and tax policy notes (optional)', { exact: true })).toHaveValue('Retained local draft');
    await page.getByRole('button', { name: 'Discard my draft and use saved version' }).click();

    // AND keyboard controls remain reachable at narrow widths in both appearances
    await page.setViewportSize({ width: 390, height: 844 });
    for (const dark of [false, true]) {
      await setAppearance(page, dark);
      await page.getByRole('button', { name: 'Policies', exact: true }).focus();
      await page.keyboard.press('Tab');
      await expect(page.getByRole('button', { name: 'Accounts and mappings', exact: true })).toBeFocused();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }

    await page.setViewportSize({ width: 320, height: 844 });
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });

    // WHEN only the reader role remains THEN the next request and deep link deny private setup
    const reader = roles.find((role: { name: string }) => role.name === 'Accounting reader');
    await assign([reader.id]);
    expect((await page.request.get('/api/beta/accounting/setup')).status()).toBe(403);
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Accounting', exact: true })).toHaveCount(0);
  } finally {
    await assign(original.roleIds);
  }
});



