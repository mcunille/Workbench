import { expect, test, type Page } from '@playwright/test';
import { useAuthenticatedSession as signIn } from './auth-fixture';

test.setTimeout(120_000);

async function saveDraft(page: Page) {
  const saved = page.waitForResponse(response => response.url().includes('/api/v2/purchase-order-drafts') &&
    ['POST', 'PUT'].includes(response.request().method()));
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  expect((await saved).ok()).toBe(true);
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();
  const detail = await page.request.get(`/api/v2/purchase-order-drafts/${page.url().split('/').at(-1)}`);
  expect(detail.ok()).toBe(true);
  return detail.json();
}

async function saveSupplier(page: Page) {
  const saved = page.waitForResponse(response => /\/api\/suppliers(?:\/[a-f0-9-]{36})?$/.test(response.url()) &&
    ['POST', 'PUT'].includes(response.request().method()));
  await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
  expect((await saved).ok()).toBe(true);
  await expect(page.getByLabel('Name', { exact: true })).toBeEnabled();
  await expect(page).toHaveURL(/\/suppliers\/[a-f0-9-]{36}$/);
}

async function selectSupplier(page: Page, name: string) {
  await page.getByRole('button', { name: 'Choose supplier', exact: true }).click();
  await page.getByLabel('Search suppliers', { exact: true }).fill(name);
  await page.getByLabel('Search suppliers', { exact: true }).press('Enter');
  await page.getByRole('button', { name: `Select ${name}`, exact: true }).click();
  await page.getByRole('button', { name: 'Use supplier', exact: true }).click();
}

test('one supplier has independent order snapshots and platforms with deliberate contact refresh', async ({ page }) => {
  // GIVEN a reusable supplier whose contact details were entered through the directory.
  await signIn(page);
  const supplierName = `Multichannel sample ${Date.now()}`;
  await page.goto('/suppliers/new');
  await page.getByLabel('Name', { exact: true }).fill(supplierName);
  await page.getByLabel('Contact name', { exact: true }).fill('Original contact');
  await page.getByLabel('Email', { exact: true }).fill('original@example.test');
  await saveSupplier(page);
  const supplierPath = new URL(page.url()).pathname;

  // WHEN purchasing through two platforms THEN both orders keep the same supplier identity.
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`Instagram ${supplierName}`);
  await page.getByLabel('Platform', { exact: true }).fill('Instagram');
  await selectSupplier(page, supplierName);
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Instagram');
  const first = await saveDraft(page);
  const firstPath = new URL(page.url()).pathname;
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`Auction ${supplierName}`);
  await selectSupplier(page, supplierName);
  await page.getByLabel('Platform', { exact: true }).fill('Gem Rock Auctions');
  const second = await saveDraft(page);
  const secondPath = new URL(page.url()).pathname;
  expect(second.draft.supplierId).toBe(first.draft.supplierId);
  expect(second.poReference).not.toBe(first.poReference);

  // WHEN the directory contact changes THEN reopening either PO still shows its saved snapshot.
  await page.goto(supplierPath);
  await page.getByLabel('Contact name', { exact: true }).fill('Updated contact');
  await page.getByLabel('Email', { exact: true }).fill('updated@example.test');
  await saveSupplier(page);
  await page.goto(firstPath);
  await page.getByText('Contact details (optional)', { exact: true }).click();
  await expect(page.getByLabel('Supplier contact name', { exact: true })).toHaveValue('Original contact');
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Instagram');

  // WHEN explicitly refreshing one order THEN only that snapshot changes, leaving its platform alone.
  await page.getByText('Supplier options', { exact: true }).click();
  await page.getByRole('button', { name: 'Use current supplier details', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Updated contact');
  await page.getByRole('button', { name: 'Replace supplier details', exact: true }).click();
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Instagram');
  await saveDraft(page);
  await page.goto(secondPath);
  await page.getByText('Contact details (optional)', { exact: true }).click();
  await expect(page.getByLabel('Supplier contact name', { exact: true })).toHaveValue('Original contact');
  await expect(page.getByLabel('Supplier email', { exact: true })).toHaveValue('original@example.test');
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Gem Rock Auctions');

  // WHEN archiving the supplier THEN existing orders retain their link and can still be edited.
  await page.goto(supplierPath);
  await page.getByRole('button', { name: 'Archive supplier', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm archive', exact: true }).click();
  await expect(page.getByText('Archived supplier', { exact: true })).toBeVisible();
  await page.goto(secondPath);
  await expect(page.getByLabel('Supplier contact name', { exact: true })).toHaveValue('Original contact');
  await page.getByLabel('Platform', { exact: true }).fill('Retail');
  const archivedOrder = await saveDraft(page);
  expect(archivedOrder.draft.supplierId).toBe(first.draft.supplierId);
  expect(archivedOrder.supplierIsArchived).toBe(true);

  // AND the directory hides it by default, but includes it on explicit request and allows reactivation.
  await page.goto('/suppliers');
  await page.getByLabel('Search suppliers', { exact: true }).fill(supplierName);
  await page.getByLabel('Search suppliers', { exact: true }).press('Enter');
  await expect(page.getByText('No matching suppliers.', { exact: true })).toBeVisible();
  await page.getByLabel('Include archived suppliers', { exact: true }).check();
  await page.getByRole('link', { name: `Edit ${supplierName}`, exact: true }).click();
  await page.getByRole('button', { name: 'Reactivate supplier', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm reactivation', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Archive supplier', exact: true })).toBeEnabled();
});

test('one-off supplier details and transaction platform persist with a permanent searchable PO reference', async ({ page }) => {
  // GIVEN an owner planning a purchase through a platform without a directory entry.
  await signIn(page);
  const title = `Platform purchase ${Date.now()}`;
  const externalReference = `IG-${Date.now()}`;
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(title);
  await page.getByLabel('Supplier name', { exact: true }).fill('Sample multichannel supplier');
  await page.getByLabel('Platform', { exact: true }).fill('Instagram');
  await page.getByLabel('Supplier order reference', { exact: true }).fill(externalReference);

  // WHEN saving and reopening THEN the platform and both distinct references survive.
  const saved = await saveDraft(page);
  expect(saved.poReference).toMatch(/^PO-\d{6,}$/);
  expect(saved.draft.supplierId).toBeNull();
  expect(saved.draft.platform).toBe('Instagram');
  const path = new URL(page.url()).pathname;
  await page.reload();
  await expect(page.getByText(saved.poReference, { exact: true })).toBeVisible();
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Instagram');
  await expect(page.getByLabel('Supplier order reference', { exact: true })).toHaveValue(externalReference);

  // WHEN changing only the platform THEN supplier details and the permanent reference remain.
  await page.getByLabel('Platform', { exact: true }).fill('Gem Rock Auctions');
  const updated = await saveDraft(page);
  expect(updated.poReference).toBe(saved.poReference);
  expect(updated.draft.supplierName).toBe(saved.draft.supplierName);
  expect(updated.draft.supplierOrderReference).toBe(externalReference);

  // WHEN searching the whole purchasing list by external reference THEN this saved order is found.
  await page.getByRole('button', { name: 'Back to purchase orders', exact: true }).click();
  await page.getByLabel('Search purchase orders', { exact: true }).fill(externalReference);
  await page.getByLabel('Search purchase orders', { exact: true }).press('Enter');
  await expect(page.getByRole('link').filter({ hasText: title })).toBeVisible();
  await expect(page.getByText('Gem Rock Auctions', { exact: true })).toBeVisible();
  await page.getByRole('link').filter({ hasText: title }).click();
  await expect(page).toHaveURL(new RegExp(`${path}$`));
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Gem Rock Auctions');
});

test('an uncertain platform save retries identical content and keeps the assigned reference', async ({ page }) => {
  // GIVEN a draft save whose server acknowledgement will be lost.
  await signIn(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`Platform retry ${Date.now()}`);
  await page.getByLabel('Platform', { exact: true }).fill('Retail');
  const requests: unknown[] = [];
  let dropped = false;
  await page.route('**/api/v2/purchase-order-drafts', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    requests.push(route.request().postDataJSON());
    if (!dropped) {
      dropped = true;
      const response = await route.fetch();
      expect(response.ok()).toBe(true);
      return route.abort('failed');
    }
    await route.continue();
  });

  // WHEN retrying the uncertain request THEN the same platform-bearing request resolves once.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await page.getByRole('button', { name: 'Check and retry', exact: true }).click();
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Retail');
  const detail = await page.request.get(`/api/v2/purchase-order-drafts/${page.url().split('/').at(-1)}`);
  const { poReference } = await detail.json();
  await page.reload();
  await expect(page.getByText(poReference, { exact: true })).toBeVisible();
});

test('inline supplier creation survives a subsequent draft failure without submitting the draft early', async ({ page }) => {
  // GIVEN an unsaved purchase and a new reusable supplier entered inside its supplier dialog.
  await signIn(page);
  await page.goto('/purchase-orders/new');
  const name = `Independent supplier ${Date.now()}`;
  await page.getByLabel('Title', { exact: true }).fill('Purchase waiting on confirmation');
  await page.getByLabel('Platform', { exact: true }).fill('Instagram');
  let draftWrites = 0;
  await page.route('**/api/v2/purchase-order-drafts', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    draftWrites++;
    await route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{"status":503}' });
  });
  await page.getByRole('button', { name: 'New supplier', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New supplier', exact: true });
  await dialog.getByLabel('Name', { exact: true }).fill(name);
  await dialog.getByLabel('Email', { exact: true }).fill('independent@example.test');

  // WHEN saving only the supplier THEN its receipt resolves independently of the unsaved PO.
  const supplierSaved = page.waitForResponse(response => response.url().endsWith('/api/suppliers') && response.request().method() === 'POST');
  await dialog.getByRole('button', { name: 'Save supplier', exact: true }).click();
  const supplierResponse = await supplierSaved;
  expect(supplierResponse.status()).toBe(201);
  const { supplierId } = await supplierResponse.json();
  await page.getByRole('button', { name: 'Use supplier', exact: true }).click();
  await expect(page.getByLabel('Supplier name', { exact: true })).toHaveValue(name);
  await expect(page.getByLabel('Platform', { exact: true })).toHaveValue('Instagram');
  expect(draftWrites).toBe(0);

  // WHEN the later PO save fails THEN local PO input and the already saved supplier are retained.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Check and retry', exact: true })).toBeVisible();
  expect(draftWrites).toBe(1);
  await expect(page.getByLabel('Title', { exact: true })).toHaveValue('Purchase waiting on confirmation');
  const supplier = await page.request.get(`/api/suppliers/${supplierId}`);
  expect(supplier.ok()).toBe(true);
  expect((await supplier.json()).supplier.name).toBe(name);
});
