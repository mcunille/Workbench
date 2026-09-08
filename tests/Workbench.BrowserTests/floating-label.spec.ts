import { expect, test } from '@playwright/test';

for (const appearance of ['light', 'dark']) {
  test(`floating labels preserve names and use a single focus border in ${appearance}`, async ({ page }) => {
    // GIVEN an empty sign-in field in the selected appearance.
    await page.goto('/');
    await page.getByRole('combobox', { name: 'Appearance' }).selectOption(appearance);
    const input = page.getByRole('textbox', { name: 'Email', exact: true });
    const label = page.getByText('Email', { exact: true });
    const labelIsRaised = async () => {
      const fieldBox = await input.boundingBox();
      const labelBox = await label.boundingBox();
      return !!fieldBox && !!labelBox && labelBox.y < fieldBox.y &&
        labelBox.y + labelBox.height > fieldBox.y;
    };
    const fieldBox = await input.boundingBox();
    const labelBox = await label.boundingBox();
    expect(labelBox!.y).toBeGreaterThan(fieldBox!.y);
    const idleBorder = await input.evaluate(el => getComputedStyle(el).borderTopColor);

    // WHEN the label is clicked THEN focus moves to its field and the label crosses the top edge.
    await label.click();
    await expect(input).toBeFocused();
    await expect.poll(labelIsRaised).toBe(true);
    expect(await input.evaluate(el => getComputedStyle(el).borderTopColor)).not.toBe(idleBorder);
    expect(await input.evaluate(el => getComputedStyle(el).borderTopWidth)).toBe('2px');
    expect(await input.boundingBox()).toEqual(fieldBox);
    expect(await input.evaluate(el => getComputedStyle(el).outlineStyle)).toBe('none');
    expect(await input.evaluate(el => getComputedStyle(el).boxShadow)).toBe('none');

    // WHEN a value is entered and focus leaves THEN the label stays raised.
    await input.fill('collector@example.test');
    await page.getByLabel('Password', { exact: true }).focus();
    await expect.poll(labelIsRaised).toBe(true);
    await expect(input).toHaveValue('collector@example.test');

    // WHEN cleared and blurred THEN the label returns inside without losing its accessible name.
    await input.fill('');
    await page.getByLabel('Password', { exact: true }).focus();
    await expect.poll(async () => (await label.boundingBox())!.y > (await input.boundingBox())!.y).toBe(true);
  });
}
