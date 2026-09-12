import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { vi } from 'vitest';
import { getSupplier, getSuppliers, type Supplier } from '../../api/suppliers';
import type { DraftContent } from '../../api/purchaseOrders';
import { DraftSupplier } from './DraftSupplier';
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), getSupplier: vi.fn(), getSuppliers: vi.fn() }));
const draft: DraftContent = { title: null, supplierName: 'Saved supplier', supplierId: 'linked', supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: 'Instagram', currency: null, notes: null, sourceLinks: [], entries: [] };
const current: Supplier = { id: 'linked', supplier: { name: 'Current supplier', contactName: null, email: null, phone: null, website: null, postalAddress: null }, isArchived: false, version: 's1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
beforeEach(() => {
  vi.mocked(getSupplier).mockReset(); vi.mocked(getSuppliers).mockReset();
  vi.mocked(getSuppliers).mockResolvedValue({ items: [], nextCursor: null });
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
});
it.each([
  ['New supplier', 'New supplier'],
  ['Choose supplier', 'Choose supplier'],
  ['Keep details as one-off', 'Keep details as one-off?'],
])('keeps the %s workflow when an earlier supplier refresh arrives late', async (action, title) => {
  // GIVEN a linked order whose current supplier refresh is still pending.
  let finish!: (supplier: Supplier) => void;
  vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const onChange = vi.fn();
  render(<DraftSupplier draft={draft} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' }));
  // WHEN the owner starts a different supplier workflow and enters local details before that read completes.
  fireEvent.click(screen.getByRole('button', { name: action }));
  const dialog = screen.getByRole('dialog', { name: title });
  if (action === 'New supplier') fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Unsaved new supplier' } });
  await act(async () => finish(current));
  // THEN the chosen workflow and unsaved input survive; the obsolete read never opens a replacement preview.
  expect(screen.getByRole('dialog', { name: title })).toBeVisible();
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  if (action === 'New supplier') expect(within(dialog).getByLabelText('Name')).toHaveValue('Unsaved new supplier');
  expect(onChange).not.toHaveBeenCalled();
});
it.each([true, false])('does not expose a stale supplier preview after the draft becomes frozen (resume: %s)', async resume => {
  // GIVEN an editable linked draft with a current supplier refresh in flight.
  let finish!: (supplier: Supplier) => void;
  vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const props = { draft, archived: false, frozen: false, onChange: vi.fn(), onAuthLost: vi.fn(), onDirtyChange: vi.fn() };
  const view = render(<DraftSupplier {...props} />);
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' }));
  // WHEN saving freezes the draft before the refresh resolves, optionally followed by editing resuming.
  view.rerender(<DraftSupplier {...props} frozen />);
  if (resume) view.rerender(<DraftSupplier {...props} />);
  await act(async () => finish(current));
  // THEN a captured save cannot be followed by stale local snapshot replacement.
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  expect(props.onChange).not.toHaveBeenCalled();
});
it('closes an existing snapshot preview when saving freezes the draft', async () => {
  // GIVEN an open reviewed supplier preview.
  vi.mocked(getSupplier).mockResolvedValueOnce(current);
  const props = { draft, archived: false, frozen: false, onChange: vi.fn(), onAuthLost: vi.fn(), onDirtyChange: vi.fn() };
  const view = render(<DraftSupplier {...props} />);
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' }));
  await screen.findByRole('dialog', { name: 'Review supplier details' });
  // WHEN a draft command freezes editing THEN the preview is dismissed and cannot resurface after editing resumes.
  view.rerender(<DraftSupplier {...props} frozen />);
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  view.rerender(<DraftSupplier {...props} />);
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  expect(props.onChange).not.toHaveBeenCalled();
});
