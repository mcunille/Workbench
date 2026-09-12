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
it('offers a compact populated confirmation when an order has no supplier details', async () => {
  // GIVEN an empty supplier section and an order-specific platform.
  vi.mocked(getSuppliers).mockResolvedValue({ items: [current], nextCursor: null });
  const onChange = vi.fn();
  render(<DraftSupplier draft={{ ...draft, supplierName: null, supplierId: null }} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  // WHEN selecting a directory supplier THEN one populated summary asks for confirmation.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  const dialog = screen.getByRole('dialog', { name: 'Use supplier?' });
  expect(within(dialog).getByText('Current supplier')).toBeVisible();
  expect(within(dialog).queryByText('Not set')).not.toBeInTheDocument();
  expect(within(dialog).queryByText('On this order')).not.toBeInTheDocument();
  expect(onChange).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Use supplier' }));
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ supplierId: current.id, platform: 'Instagram' }));
});
it('requires a reference decision even when the supplier contact section is empty', async () => {
  // GIVEN a reference-only order with no current supplier details.
  vi.mocked(getSuppliers).mockResolvedValue({ items: [current], nextCursor: null });
  const onChange = vi.fn();
  render(<DraftSupplier draft={{ ...draft, supplierName: null, supplierId: null, supplierOrderReference: 'OLD-1' }} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  // WHEN selecting a supplier THEN the compact confirmation still requires an explicit reference choice.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  const apply = screen.getByRole('button', { name: 'Use supplier' });
  expect(apply).toBeDisabled();
  fireEvent.click(screen.getByLabelText('Clear supplier order reference'));
  fireEvent.click(apply);
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ supplierOrderReference: null, platform: 'Instagram' }));
});
it('compares only changed fields and makes removed contact details explicit', async () => {
  // GIVEN a linked supplier whose saved email was removed from the directory.
  vi.mocked(getSupplier).mockResolvedValue(current);
  render(<DraftSupplier draft={{ ...draft, supplierEmail: 'old@example.test' }} archived={false} frozen={false} onChange={vi.fn()} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  // WHEN explicitly refreshing THEN changed names and the email removal are visible, without empty phone rows.
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' }));
  const dialog = await screen.findByRole('dialog', { name: 'Review supplier details' });
  expect(within(dialog).getByText('old@example.test')).toBeVisible();
  expect(within(dialog).getByText('Will be cleared')).toBeVisible();
  expect(within(dialog).queryByText('Phone')).not.toBeInTheDocument();
  expect(within(dialog).queryByText('Not set')).not.toBeInTheDocument();
});
it('keeps explicit refresh deliberate even when the contact details already match', async () => {
  // GIVEN a linked order already using the current supplier name and empty contacts.
  vi.mocked(getSupplier).mockResolvedValue(current);
  const onChange = vi.fn();
  render(<DraftSupplier draft={{ ...draft, supplierName: current.supplier.name }} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  // WHEN requesting a refresh THEN matching details are summarized once without pretending fields changed.
  fireEvent.click(screen.getByText('Supplier options'));
  fireEvent.click(screen.getByRole('button', { name: 'Use current supplier details' }));
  const dialog = await screen.findByRole('dialog', { name: 'Review supplier details' });
  expect(within(dialog).getByText('Your contact details already match this supplier.')).toBeVisible();
  expect(within(dialog).queryByText('Not set')).not.toBeInTheDocument();
  expect(onChange).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
  expect(onChange).not.toHaveBeenCalled();
});
it('protects inline supplier edits through the single cancel action', () => {
  // GIVEN local supplier input in the new supplier dialog.
  render(<DraftSupplier draft={draft} archived={false} frozen={false} onChange={vi.fn()} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'New supplier' }));
  const dialog = screen.getByRole('dialog', { name: 'New supplier' });
  fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Unsaved studio' } });
  // WHEN cancelling from the shared footer THEN the discard choice retains input on return.
  expect(within(dialog).getAllByRole('button', { name: 'Cancel' })).toHaveLength(1);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing supplier' }));
  expect(within(dialog).getByLabelText('Name')).toHaveValue('Unsaved studio');
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
