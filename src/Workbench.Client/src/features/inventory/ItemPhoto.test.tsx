import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { ItemPhoto } from './ItemPhoto';
import { getPhoto } from '../../api/items';
import { ApiError } from '../../api/auth';
vi.mock('../../api/items', () => ({ getPhoto: vi.fn() }));
it('ends the session when image delivery loses authorization', async () => {
  // GIVEN an expired session on the authenticated image endpoint.
  vi.mocked(getPhoto).mockRejectedValue(new ApiError(401));
  const lost = vi.fn();
  // WHEN displaying the photo THEN report authentication loss.
  render(<ItemPhoto url="/photo" name="Stone" onAuthLost={lost} />);
  await waitFor(() => expect(lost).toHaveBeenCalledOnce());
  expect(await screen.findByRole('status')).toHaveTextContent(
    'Photograph unavailable',
  );
});
it('shows a recoverable image error without ending a valid session', async () => {
  // GIVEN unavailable storage WHEN loading THEN retain the valid session.
  vi.mocked(getPhoto).mockRejectedValue(new ApiError(503));
  const lost = vi.fn();
  render(<ItemPhoto interactive url="/photo" name="Stone" onAuthLost={lost} />);
  expect(
    await screen.findByRole('button', { name: 'Retry photograph' }),
  ).toBeVisible();
  expect(lost).not.toHaveBeenCalled();
});
