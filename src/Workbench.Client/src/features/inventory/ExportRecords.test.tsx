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
