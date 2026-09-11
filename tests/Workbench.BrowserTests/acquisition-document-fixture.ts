import { type Page } from '@playwright/test';

export async function receiptImage(page: Page) {
  const data = await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 600; canvas.height = 400;
    const context = canvas.getContext('2d')!;
    context.fillStyle = '#fff'; context.fillRect(0, 0, 600, 400);
    context.fillStyle = '#222'; context.font = '24px sans-serif';
    context.fillText('SAMPLE ACQUISITION RECORD', 30, 70);
    context.font = '18px sans-serif'; context.fillText('Autumn mineral fair — synthetic evidence', 30, 120);
    context.fillText('Blue sapphire; collector notes', 30, 165);
    return canvas.toDataURL('image/png').split(',')[1];
  });
  return { name: 'sample-receipt.png', mimeType: 'image/png', buffer: Buffer.from(data, 'base64') };
}
