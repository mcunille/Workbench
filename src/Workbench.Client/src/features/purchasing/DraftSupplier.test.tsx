import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { vi } from 'vitest';
import { getSupplier, getSuppliers, type Supplier } from '../../api/suppliers';
import type { DraftContent } from '../../api/purchaseOrders';
import { DraftSupplier } from './DraftSupplier';
vi.mock('../../api/suppliers', async original => ({ ...await original<typeof import('../../api/suppliers')>(), getSupplier: vi.fn(), getSuppliers: vi.fn() }));
const draft: DraftContent = { title: null, supplierName: 'Saved supplier', supplierId: 'linked', supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null, supplierPostalAddress: null, supplierOrderReference: null, platform: 'Instagram', currency: null, notes: null, sourceLinks: [], entries: [] };
const current: Supplier = { id: 'linked', supplier: { name: 'Current supplier', contactName: null, email: null, phone: null, website: null, postalAddress: null }, isArchived: false, version: 's1', createdAtUtc: '2026-09-12T00:00:00Z', updatedAtUtc: '2026-09-12T00:00:00Z' };
beforeEach(() => {
  vi.mocked(getSupplier).mockReset(); vi.mocked(getSupplier).mockResolvedValue(current); vi.mocked(getSuppliers).mockReset();
  vi.mocked(getSuppliers).mockResolvedValue({ items: [current], nextCursor: null });
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
  const dialog = await screen.findByRole('dialog', { name: 'Use supplier?' });
  expect(within(dialog).getByText('Current supplier')).toBeVisible();
  expect(within(dialog).queryByText('Not set')).not.toBeInTheDocument();
  expect(within(dialog).queryByText('On this order')).not.toBeInTheDocument();
  expect(onChange).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole('button', { name: 'Use supplier details' }));
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
  const apply = await screen.findByRole('button', { name: 'Use supplier details' });
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
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  const dialog = await screen.findByRole('dialog', { name: 'Review supplier details' });
  expect(within(dialog).getByText('old@example.test')).toBeVisible();
  expect(within(dialog).getByText('Will be cleared')).toBeVisible();
  expect(within(dialog).queryByText('Phone')).not.toBeInTheDocument();
  expect(within(dialog).queryByText('Not set')).not.toBeInTheDocument();
});
it('applies matching supplier details without another confirmation', async () => {
  // GIVEN matching PO and directory details.
  const onChange = vi.fn();
  render(<DraftSupplier draft={{ ...draft, supplierName: current.supplier.name }} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  // WHEN selecting that supplier THEN it applies directly, preserving the platform.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  await act(async () => {});
  expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ supplierId: current.id, platform: 'Instagram' }));
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
});
it('offers only cancel or copying the selected supplier details when contacts differ', async () => {
  // GIVEN a one-off snapshot that differs from the chosen directory supplier.
  const onChange = vi.fn();
  render(<DraftSupplier draft={{ ...draft, supplierId: null, supplierEmail: 'po@example.test' }} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  // WHEN reviewing selection THEN the only actions are Cancel and Use supplier details.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  let dialog = await screen.findByRole('dialog', { name: 'Review supplier details' });
  expect(within(dialog).getAllByRole('button').map(button => button.textContent)).toEqual(['Cancel', 'Use supplier details']);
  // WHEN cancelling THEN neither the link nor the snapshot changes.
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
  expect(onChange).not.toHaveBeenCalled();
  // WHEN accepting the selection THEN both identity and contact snapshot follow the selected supplier.
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  dialog = await screen.findByRole('dialog', { name: 'Review supplier details' });
  fireEvent.click(within(dialog).getByRole('button', { name: 'Use supplier details' }));
  expect(onChange).toHaveBeenCalledWith({ ...draft, supplierId: current.id, supplierName: current.supplier.name, supplierEmail: null });
});it('protects inline supplier edits through the single cancel action', () => {
  // GIVEN local supplier input in the new supplier dialog.
  render(<DraftSupplier draft={draft} archived={false} frozen={false} onChange={vi.fn()} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'New supplier' }));
  const dialog = screen.getByRole('dialog', { name: 'New supplier' });
  fireEvent.change(within(dialog).getByLabelText('Supplier name'), { target: { value: 'Unsaved studio' } });
  // WHEN cancelling from the shared footer THEN the discard choice retains input on return.
  expect(within(dialog).getAllByRole('button', { name: 'Cancel' })).toHaveLength(1);
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
  fireEvent.click(screen.getByRole('button', { name: 'Keep editing supplier' }));
  expect(within(dialog).getByLabelText('Supplier name')).toHaveValue('Unsaved studio');
});
it.each([
  ['New supplier', 'New supplier'],
  ['Choose supplier', 'Choose supplier'],
])('keeps the %s workflow when an earlier supplier refresh arrives late', async (action, title) => {
  // GIVEN a linked order whose current supplier refresh is still pending.
  let finish!: (supplier: Supplier) => void;
  vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const onChange = vi.fn();
  render(<DraftSupplier draft={draft} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  // WHEN the owner starts a different supplier workflow and enters local details before that read completes.
  fireEvent.click(screen.getByRole('button', { name: action }));
  const dialog = screen.getByRole('dialog', { name: title });
  if (action === 'New supplier') fireEvent.change(within(dialog).getByLabelText('Supplier name'), { target: { value: 'Unsaved new supplier' } });
  await act(async () => finish(current));
  // THEN the chosen workflow and unsaved input survive; the obsolete read never opens a replacement preview.
  expect(screen.getByRole('dialog', { name: title })).toBeVisible();
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  if (action === 'New supplier') expect(within(dialog).getByLabelText('Supplier name')).toHaveValue('Unsaved new supplier');
  expect(onChange).not.toHaveBeenCalled();
});
it.each([true, false])('does not expose a stale supplier preview after the draft becomes frozen (resume: %s)', async resume => {
  // GIVEN an editable linked draft with a current supplier refresh in flight.
  let finish!: (supplier: Supplier) => void;
  vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const props = { draft, archived: false, frozen: false, onChange: vi.fn(), onAuthLost: vi.fn(), onDirtyChange: vi.fn() };
  const view = render(<DraftSupplier {...props} />);
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
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
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  await screen.findByRole('dialog', { name: 'Review supplier details' });
  // WHEN a draft command freezes editing THEN the preview is dismissed and cannot resurface after editing resumes.
  view.rerender(<DraftSupplier {...props} frozen />);
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  view.rerender(<DraftSupplier {...props} />);
  expect(screen.queryByRole('dialog', { name: 'Review supplier details' })).not.toBeInTheDocument();
  expect(props.onChange).not.toHaveBeenCalled();
});
it('preserves edits made while the selected supplier is loading', async () => {
  // GIVEN matching details and an in-flight selection read.
  let finish!: (supplier: Supplier) => void;
  vi.mocked(getSupplier).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
  const props = { draft: { ...draft, supplierName: current.supplier.name }, archived: false, frozen: false, onChange: vi.fn(), onAuthLost: vi.fn(), onDirtyChange: vi.fn() };
  const view = render(<DraftSupplier {...props} />);
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  // WHEN local contact and platform edits arrive before the response.
  view.rerender(<DraftSupplier {...props} draft={{ ...props.draft, supplierPhone: 'New local phone', platform: 'Retail' }} />);
  await act(async () => finish(current));
  // THEN the changed contact requires a decision; cancelling does not overwrite local edits.
  expect(props.onChange).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(props.onChange).not.toHaveBeenCalled();
});
it.each(['keep', 'clear'])('requires the reference decision when changing supplier (%s)', async choice => {
  // GIVEN a supplier change with an existing external order reference.
  const onChange = vi.fn();
  render(<DraftSupplier draft={{ ...draft, supplierId: 'previous', supplierOrderReference: 'OLD-42' }} archived={false} frozen={false} onChange={onChange} onAuthLost={vi.fn()} onDirtyChange={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Choose supplier' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Current supplier' }));
  const apply = await screen.findByRole('button', { name: 'Use supplier details' });
  // THEN accepting the supplier cannot silently carry the old reference.
  expect(apply).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Use supplier details' })).toBeDisabled();
  // WHEN explicitly choosing the reference policy THEN the snapshot and link both change.
  fireEvent.click(screen.getByLabelText(choice === 'keep' ? 'Keep supplier order reference' : 'Clear supplier order reference'));
  fireEvent.click(apply);
  expect(onChange).toHaveBeenCalledWith({ ...draft, supplierId: current.id, supplierName: current.supplier.name, supplierOrderReference: choice === 'keep' ? 'OLD-42' : null });
});
