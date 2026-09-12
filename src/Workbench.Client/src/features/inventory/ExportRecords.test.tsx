import { act, fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { ExportRecords } from './ExportRecords';
import { ExportMemory } from './exportMemory';
import { prepareExport } from '../../api/export';

vi.mock('../../api/export', () => ({ prepareExport: vi.fn() }));
beforeEach(() => {
  vi.mocked(prepareExport).mockReset();
  URL.createObjectURL = vi.fn(() => 'blob:export');
  URL.revokeObjectURL = vi.fn();
});
it('offers accessible package selection, limits, and ZIP download with CSV recovery', async () => {
  // GIVEN the CSV-compatible default and explicit package selection.
  vi.mocked(prepareExport).mockResolvedValue({ blob: new Blob(['package']), filename: 'package.zip' });
  const memory = new ExportMemory();
  render(<ExportRecords memory={memory} follow={vi.fn()} onAuthLost={vi.fn()} />);
  expect(screen.getByRole('radio', { name: 'Records (CSV)' })).toBeChecked();
  expect(screen.getByText(/CSV includes acquisition facts/)).toHaveTextContent('photographs and acquisition documents require ZIP');
  fireEvent.click(screen.getByRole('radio', { name: 'Records, photographs and acquisition documents (ZIP)' }));
  expect(screen.getByText(/Shared acquisition documents appear once/)).toHaveTextContent('outside your chosen scope');
  expect(screen.getByText(/10,000 documents/)).toBeVisible();
  expect(screen.getByText(/128 MiB/)).toBeVisible();
  expect(screen.getByText(/camera originals/)).toBeVisible();
  fireEvent.click(screen.getByRole('radio', { name: 'Active records' }));
  // WHEN preparation completes THEN ZIP download is available.
  fireEvent.click(screen.getByRole('button', { name: 'Prepare export' }));
  expect(await screen.findByRole('link', { name: 'Download ZIP' })).toHaveAttribute('download', 'package.zip');
  // WHEN selecting CSV THEN the old package is discarded and CSV facts return.
  fireEvent.click(screen.getByRole('radio', { name: 'Records (CSV)' }));
  expect(screen.queryByRole('link', { name: 'Download ZIP' })).not.toBeInTheDocument();
  expect(screen.getByText('CSV version 2 · UTF-8')).toBeVisible();
  memory.dispose();
});
it('requires accessible explicit scope, reports completeness, and preserves a file across page remounts', async () => {
  // GIVEN an export page with no implicit scope.
  const memory = new ExportMemory();
  const props = { memory, follow: vi.fn(), onAuthLost: vi.fn() };
  const view = render(<ExportRecords {...props} />);
  expect(screen.getByRole('button', { name: 'Prepare export' })).toBeDisabled();
  expect(screen.getByRole('radio', { name: 'Active records' })).not.toBeChecked();
  expect(screen.getByRole('radio', { name: 'Active and archived records' })).not.toBeChecked();
  let finish!: (value: { blob: Blob; filename: string }) => void;
  vi.mocked(prepareExport).mockReturnValue(new Promise(resolve => { finish = resolve; }));
  // WHEN choosing all records and preparing THEN no download appears before completion.
  fireEvent.click(screen.getByRole('radio', { name: 'Active and archived records' }));
  fireEvent.click(screen.getByRole('button', { name: 'Prepare export' }));
  expect(screen.queryByRole('link', { name: 'Download CSV' })).not.toBeInTheDocument();
  expect(screen.getByRole('status')).toHaveTextContent('Preparing export');
  expect(screen.getByRole('button', { name: 'Cancel' })).toBeEnabled();
  await act(async () => finish({ blob: new Blob(['complete']), filename: 'records.csv' }));
  expect(screen.getByRole('link', { name: 'Download CSV' })).toHaveAttribute('download', 'records.csv');
  // WHEN navigating away and back THEN private application memory retains the prepared file.
  view.unmount();
  render(<ExportRecords {...props} />);
  expect(screen.getByRole('radio', { name: 'Active and archived records' })).toBeChecked();
  expect(screen.getByRole('link', { name: 'Download CSV' })).toHaveAttribute('href', 'blob:export');
  // WHEN changing scope THEN the old file can no longer be downloaded.
  fireEvent.click(screen.getByRole('radio', { name: 'Active records' }));
  expect(screen.queryByRole('link', { name: 'Download CSV' })).not.toBeInTheDocument();
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:export');
  memory.dispose();
});
it('provides safe retry after a failed body read', async () => {
  // GIVEN a failed preparation with no usable file.
  vi.mocked(prepareExport).mockRejectedValueOnce(new Error('body failed')).mockResolvedValueOnce(null);
  const memory = new ExportMemory();
  render(<ExportRecords memory={memory} follow={vi.fn()} onAuthLost={vi.fn()} />);
  fireEvent.click(screen.getByRole('radio', { name: 'Active records' }));
  fireEvent.click(screen.getByRole('button', { name: 'Prepare export' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('records may have changed');
  // WHEN retrying THEN an empty result is explicit and no download exists.
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
  expect(await screen.findByText(/There are no records in the selected scope/)).toBeVisible();
  expect(screen.queryByRole('link', { name: 'Download CSV' })).not.toBeInTheDocument();
  memory.dispose();
});
