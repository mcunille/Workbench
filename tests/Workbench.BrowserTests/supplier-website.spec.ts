import { expect, test } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';

test('supplier websites accept domains on create and edit and remain external saved links', async ({ page, context }) => {
  test.setTimeout(120_000);
  // GIVEN a supplier entered in the real form, including surrounding whitespace.
  await useAuthenticatedSession(page);
  await page.goto('/suppliers/new');
  const name = `Website sample ${Date.now()}`;
  await page.getByLabel('Supplier name', { exact: true }).fill(name);
  const website = page.getByLabel('Website', { exact: true });
  const save = page.getByRole('button', { name: 'Save supplier', exact: true });
  await website.fill(' example.com ');
  // WHEN saving THEN native validation allows the domain and a reload shows the normalized URL.
  await save.click();
  await expect(page).toHaveURL(/\/suppliers\/[a-f0-9-]{36}$/);
  await expect(website).toHaveValue('https://example.com');
  await page.reload();
  await expect(website).toHaveValue('https://example.com');

  // WHEN editing THEN explicit schemes, blank input and scheme-less paths round-trip through the server.
  for (const [input, expected] of [
    [' http://example.com/shop?q=one#two ', 'http://example.com/shop?q=one#two'],
    ['https://example.com/secure', 'https://example.com/secure'],
    ['', ''],
    [' www.example.com/shop?q=gem%20stone#stock ', 'https://www.example.com/shop?q=gem%20stone#stock'],
  ]) {
    await website.fill(input);
    await save.click();
    await expect(save).toBeDisabled();
    await expect(website).toBeEnabled();
    await expect(website).toHaveValue(expected);
  }
  const supplierPath = new URL(page.url()).pathname;
  // AND malformed and unsupported input receives accessible server feedback without replacing saved data.
  for (const invalid of ['not a website', 'javascript:alert(1)']) {
    await website.fill(invalid);
    await save.click();
    await expect(website).toHaveAttribute('aria-invalid', 'true');
    await expect(website).toBeFocused();
    await expect(website).toHaveAccessibleDescription(/HTTP or HTTPS/);
    await expect(website).toHaveValue(invalid);
  }
  await page.reload();
  const url = 'https://www.example.com/shop?q=gem%20stone#stock';
  await expect(website).toHaveValue(url);

  // WHEN the saved supplier is used on an ordered purchase THEN its website is an external link.
  await page.goto('/purchase-orders/new');
  await page.getByRole('button', { name: 'Choose supplier', exact: true }).click();
  await page.getByLabel('Search suppliers', { exact: true }).fill(name);
  await page.getByRole('button', { name: `Select ${name}`, exact: true }).click();
  await page.getByRole('button', { name: 'Use supplier details', exact: true }).click();
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 1', { exact: true }).fill('Sample stone');
  await page.getByLabel('Quantity 1', { exact: true }).fill('1');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await page.getByRole('button', { name: 'Record as ordered', exact: true }).click();
  await page.getByLabel('Order date', { exact: true }).fill('2026-09-20');
  await page.getByRole('button', { name: 'Confirm order', exact: true }).click();
  await page.getByLabel('Order and supplier details', { exact: true }).click();
  const link = page.getByRole('link', { name: url, exact: true });
  await expect(link).toHaveAttribute('href', url);
  await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  // Intercept only the external destination so this check does not depend on a third-party website.
  await context.route('https://www.example.com/**', route => route.fulfill({ contentType: 'text/html', body: '<h1>Supplier website</h1>' }));
  const opened = page.waitForEvent('popup');
  await link.click();
  const popup = await opened;
  await expect(popup).toHaveURL(url);
  await popup.close();
  await page.goto(supplierPath);
  await expect(website).toHaveValue(url);
});
