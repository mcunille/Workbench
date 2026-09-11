import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { findAcquisitions } from '../../api/acquisitions';
import { AcquisitionPicker } from './AcquisitionPicker';
vi.mock('../../api/acquisitions', async original => ({ ...await original<typeof import('../../api/acquisitions')>(), findAcquisitions: vi.fn() }));
const acquisition = { id: '12345678-fair', source: 'Autumn fair', method: 'Purchase', year: 2025, month: 9, day: null, notes: 'Empty but retained', version: 'a1' };
it('distinguishes loading, failed, empty, and selectable saved acquisitions', async () => {
  // GIVEN discovery initially fails rather than reporting no saved acquisitions.
  vi.mocked(findAcquisitions).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValueOnce({ items: [], nextCursor: null })
    .mockResolvedValue({ items: [acquisition], nextCursor: null });
  const onSelect = vi.fn();
  render(<AcquisitionPicker onSelect={onSelect} onCancel={vi.fn()} onAuthLost={vi.fn()} />);
  expect(screen.getByText('Loading acquisitions…')).toBeVisible();
  await screen.findByText('We could not load acquisitions.');
  expect(screen.queryByText('No acquisitions found.')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Retry loading acquisitions' }));
  await screen.findByText('No acquisitions found.');
  // WHEN searching saved source and notes THEN selectable results show event identity and partial date.
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search acquisitions' }), { target: { value: ' retained ' } });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Select Autumn fair · Purchase · 2025-09 · 12345678' }));
  expect(findAcquisitions).toHaveBeenLastCalledWith('retained');
  expect(onSelect).toHaveBeenCalledExactlyOnceWith(acquisition);
});
it('retries a failed next page without losing earlier results', async () => {
  // GIVEN a loaded first page and a temporary next-page failure.
  vi.mocked(findAcquisitions).mockReset().mockResolvedValueOnce({ items: [acquisition], nextCursor: 'more' })
    .mockRejectedValueOnce(new TypeError('Network')).mockResolvedValue({ items: [{ ...acquisition, id: 'second', source: 'Next fair' }], nextCursor: null });
  render(<AcquisitionPicker onSelect={vi.fn()} onCancel={vi.fn()} onAuthLost={vi.fn()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Load more acquisitions' }));
  await screen.findByText('We could not load acquisitions.');
  expect(screen.getByRole('button', { name: /^Select Autumn fair/ })).toBeVisible();
  // WHEN retrying THEN concatenate the requested page exactly once.
  fireEvent.click(screen.getByRole('button', { name: 'Retry loading acquisitions' }));
  await screen.findByRole('button', { name: /^Select Next fair/ });
  await waitFor(() => expect(findAcquisitions).toHaveBeenLastCalledWith('', 'more'));
  expect(screen.getAllByRole('button', { name: /^Select / })).toHaveLength(2);
});
