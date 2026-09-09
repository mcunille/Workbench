import { expect, type Page } from '@playwright/test';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { inflateRawSync } from 'node:zlib';
import { parseExportCsv } from './export-fixture';
import { cameraImage } from './photo-fixture';

type PackageManifest = {
  package_version: number; scope: string; exported_at_utc: string; record_count: number; photo_count: number;
  items: { item_id: string; name: string; location: string | null; photo: { status: string; path?: string; media_type?: string; byte_length?: number; sha256?: string } }[];
};

// Independent ZIP central-directory reader using only Node primitives. No server encoding helper.
// Package v1 is bounded below ZIP64 sizes and uses only fixed files and canonical UUID photo paths.
export function inspectPackage(bytes: Buffer) {
  let end = bytes.length - 22;
  while (end >= Math.max(0, bytes.length - 65557) && bytes.readUInt32LE(end) !== 0x06054b50) end--;
  expect(end).toBeGreaterThanOrEqual(0);
  expect(end + 22 + bytes.readUInt16LE(end + 20)).toBe(bytes.length);
  const count = bytes.readUInt16LE(end + 10);
  let cursor = bytes.readUInt32LE(end + 16);
  const entries = new Map<string, Buffer>();
  for (let i = 0; i < count; i++) {
    expect(bytes.readUInt32LE(cursor)).toBe(0x02014b50);
    const method = bytes.readUInt16LE(cursor + 10);
    const compressed = bytes.readUInt32LE(cursor + 20);
    const length = bytes.readUInt32LE(cursor + 24);
    const nameLength = bytes.readUInt16LE(cursor + 28);
    const name = bytes.subarray(cursor + 46, cursor + 46 + nameLength).toString('utf8');
    expect(name).toMatch(/^(README\.txt|records\.csv|manifest\.json|photos\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.webp)$/);
    expect(entries.has(name)).toBe(false);
    const local = bytes.readUInt32LE(cursor + 42);
    expect(bytes.readUInt32LE(local)).toBe(0x04034b50);
    const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
    expect([0, 8]).toContain(method);
    const content = method === 0 ? bytes.subarray(start, start + compressed) : inflateRawSync(bytes.subarray(start, start + compressed));
    expect(content.length).toBe(length);
    entries.set(name, content);
    cursor += 46 + nameLength + bytes.readUInt16LE(cursor + 30) + bytes.readUInt16LE(cursor + 32);
  }
  expect(cursor).toBe(end);
  const manifest = JSON.parse(entries.get('manifest.json')!.toString('utf8')) as PackageManifest;
  const records = parseExportCsv(entries.get('records.csv')!);
  expect(manifest.package_version).toBe(1);
  expect(manifest.record_count).toBe(records.length);
  expect(manifest.items).toHaveLength(records.length);
  expect(new Set(manifest.items.map(item => item.item_id)).size).toBe(records.length);
  expect(manifest.photo_count).toBe(manifest.items.filter(item => item.photo.status === 'included').length);
  expect(entries.size).toBe(3 + manifest.photo_count);
  for (const item of manifest.items) {
    const record = records.find(row => row[3] === item.item_id)!;
    expect(record).toBeDefined();
    expect(record[1]).toBe(manifest.exported_at_utc);
    expect(record[2]).toBe(manifest.scope);
    expect(record[5].slice(1)).toBe(item.name);
    expect(record[7] === '' ? null : record[7].slice(1)).toBe(item.location);
    if (item.photo.status === 'included') {
      expect(item.photo.path).toBe(`photos/${item.item_id}.webp`);
      const photo = entries.get(item.photo.path!)!;
      expect(photo).toBeDefined();
      expect(item.photo.media_type).toBe('image/webp');
      expect(photo.length).toBe(item.photo.byte_length);
      expect(createHash('sha256').update(photo).digest('hex')).toBe(item.photo.sha256!.toLowerCase());
      expect(photo.subarray(0, 4).toString()).toBe('RIFF');
      expect(photo.subarray(8, 12).toString()).toBe('WEBP');
    } else expect(item.photo).toEqual({ status: 'none' });
  }
  expect(entries.get('README.txt')!.toString()).toContain('WebP');
  return { entries, manifest, records };
}

export async function uploadPackagePhoto(page: Page, id: string) {
  await page.goto(`/inventory/${id}`);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByText('Current saved photograph loaded.', { exact: true })).toBeVisible();
  const detail = await (await page.request.get(`/api/items/${id}`)).json();
  const response = await page.request.get(detail.photo.detailUrl);
  expect(response.ok()).toBe(true);
  return { detail, bytes: await response.body() };
}

export async function downloadPackage(page: Page, saveAs?: string) {
  const event = page.waitForEvent('download');
  await page.getByRole('link', { name: 'Download ZIP', exact: true }).click();
  const download = await event;
  expect(download.suggestedFilename()).toMatch(/^workbench-package-v1-(active|all)-[0-9TZ.\-]+\.zip$/);
  expect(await download.failure()).toBeNull();
  if (saveAs) await download.saveAs(saveAs);
  const path = await download.path(); expect(path).not.toBeNull();
  return inspectPackage(await readFile(path!));
}
