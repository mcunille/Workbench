import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { StrictMode } from 'react';
import { vi } from 'vitest';
import { consumeInvitation, consumeRecovery } from '../../api/auth';
import { Recovery } from './Recovery';

vi.mock('../../api/auth', () => ({
  consumeInvitation: vi.fn(),
  consumeRecovery: vi.fn(),
  requestRecovery: vi.fn(),
}));

describe('Recovery', () => {
  it('presents recovery with the sign-in branding and email form', () => {
    // GIVEN a visitor who forgot their password.
    // WHEN the recovery request page is rendered.
    render(<Recovery />);

    // THEN the sign-in identity appears above the accessible email form.
    expect(screen.getByRole('img', { name: 'Workbench' })).toBeVisible();
    expect(screen.getByText('by The White Stag Collection')).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Recover account' })).toBeVisible();
    expect(screen.getByLabelText('Email')).toHaveAttribute('type', 'email');
    expect(screen.getByRole('button', { name: 'Recover account' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Return to sign in' })).toHaveAttribute('href', '/');
  });

  it.each([false, true])('identifies the product on public account pages (invitation=%s)', (invitation) => {
    // GIVEN a visitor opening recovery or an invitation without a token.
    // WHEN the public account page is rendered.
    render(<Recovery invitation={invitation} />);
    // THEN its wordmark exposes the full product name, including the graphical W.
    expect(screen.getByRole('img', { name: 'Workbench' })).toBeVisible();
  });
  it.each([false, true])('retains the in-memory capability through failure and retry (invitation=%s)', async (invitation) => {
    // GIVEN a scrubbed URL and a transient capability under StrictMode
    window.history.replaceState(null, '', invitation ? '/invite' : '/recover');
    const consume = vi.mocked(invitation ? consumeInvitation : consumeRecovery);
    consume.mockReset().mockRejectedValueOnce(new Error('temporary')).mockResolvedValueOnce(undefined);
    const { rerender } = render(<StrictMode><Recovery invitation={invitation} token="sentinel" /></StrictMode>);
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'Valid password 1!' } });
    // WHEN consumption fails and the user retries after a render
    fireEvent.click(screen.getByRole('button'));
    await screen.findByRole('alert');
    rerender(<StrictMode><Recovery invitation={invitation} token="sentinel" /></StrictMode>);
    fireEvent.click(screen.getByRole('button'));
    // THEN the same token reaches the correct endpoint and the URL stays clean
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Your password has been set'));
    expect(consume).toHaveBeenCalledTimes(2);
    expect(consume).toHaveBeenNthCalledWith(1, 'sentinel', 'Valid password 1!');
    expect(consume).toHaveBeenNthCalledWith(2, 'sentinel', 'Valid password 1!');
    expect(window.location.search).toBe('');
  });

  it('does not turn an invitation without a token into account recovery', () => {
    window.history.pushState({}, '', '/invite');

    render(<Recovery invitation />);

    // THEN even an invalid invitation retains the shared Workbench identity.
    expect(screen.getByRole('img', { name: 'Workbench' })).toBeVisible();
    expect(screen.queryByText('by The White Stag Collection')).not.toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent(
      'This invitation link is missing its token.',
    );
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument();
  });
});
