import { fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import { Collection } from './Collection';
import { getItems } from '../../api/items';

vi.mock('../../api/items', () => ({ getItems: vi.fn() }));

it('switches collection presentation without losing loaded records or fetching again', async () => {
  // GIVEN an existing collection with a second page available.
  vi.mocked(getItems).mockResolvedValue({
    items: [
      {
        id: 'sapphire',
        name: 'Blue sapphire',
        location: null,
        createdAtUtc: '2026-09-06T00:00:00Z',
      },
    ],
    nextCursor: 'next',
  });
  render(<Collection follow={vi.fn()} onAuthLost={vi.fn()} />);
  const link = await screen.findByRole('link', { name: /Blue sapphire/ });
  expect(screen.getByRole('button', { name: 'Grid', pressed: true })).toBeVisible();
  // WHEN the collector selects the compact list.
  fireEvent.click(screen.getByRole('button', { name: 'List' }));
  // THEN the same link and pagination remain, with only presentation changed.
  expect(screen.getByRole('button', { name: 'List', pressed: true })).toBeVisible();
  expect(link).toHaveAttribute('href', '/inventory/sapphire');
  expect(link.closest('ul')).toHaveAttribute('data-view', 'list');
  expect(screen.getByRole('button', { name: 'Load more' })).toBeEnabled();
  expect(getItems).toHaveBeenCalledTimes(1);
  // WHEN returning to the gallery, the record remains available.
  fireEvent.click(screen.getByRole('button', { name: 'Grid' }));
  expect(link.closest('ul')).toHaveAttribute('data-view', 'grid');
  expect(getItems).toHaveBeenCalledTimes(1);
});
