import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { PurchaseDocumentDisposalDialog } from './PurchaseDocumentDisposalDialog';

const file = { id: 'file-1', label: 'Private invoice', mediaType: 'application/pdf', extension: 'pdf', length: 256, createdAtUtc: '2026-09-18T00:00:00Z', version: 'd1', unavailable: false };
beforeEach(() => { HTMLDialogElement.prototype.showModal = function () { this.open = true; }; });

it('requires a reason, warns about bytes, and restores focus after closing', async () => {
  // GIVEN a focused launch control and a retained document.
  const onClose = vi.fn(); const onConfirm = vi.fn();
  const launch = document.createElement('button');
  document.body.appendChild(launch); launch.focus();
  const view = render(<PurchaseDocumentDisposalDialog document={file} onConfirm={onConfirm} onClose={onClose} busy={false} error={null} />);
  // WHEN opened THEN the file and permanent-byte warning are named, and blank submission is blocked.
  expect(screen.getByRole('dialog')).toHaveAccessibleName(/Dispose retained document/);
  expect(screen.getByText(/Private invoice/)).toBeVisible();
  expect(screen.getByText(/permanent/i)).toBeVisible();
  expect(screen.getByRole('button', { name: 'Dispose document' })).toBeDisabled();
  fireEvent.change(screen.getByRole('textbox', { name: 'Reason for disposal' }), { target: { value: '  Retention expired  ' } });
  fireEvent.click(screen.getByRole('button', { name: 'Dispose document' }));
  expect(onConfirm).toHaveBeenCalledWith('  Retention expired  ');
  // THEN closing returns keyboard focus to the launcher.
  view.unmount();
  await waitFor(() => expect(launch).toHaveFocus());
  launch.remove();
});
