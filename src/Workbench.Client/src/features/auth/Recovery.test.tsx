import { render, screen } from '@testing-library/react';
import { Recovery } from './Recovery';

describe('Recovery', () => {
  it('accepts a recovery token from the URL fragment', () => {
    // GIVEN a recovery link whose token is never part of the HTTP request URL.
    window.history.pushState({}, '', '/recover#token=test-token');
    // WHEN the recovery form is opened.
    render(<Recovery />);
    // THEN it offers password reset rather than requesting an email address.
    expect(screen.getByRole('heading', { name: 'Reset password' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument();
  });

  it('does not turn an invitation without a token into account recovery', () => {
    window.history.pushState({}, '', '/invite');

    render(<Recovery invitation />);

    expect(screen.getByRole('alert')).toHaveTextContent(
      'This invitation link is missing its token.',
    );
    expect(screen.queryByRole('form')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Email')).not.toBeInTheDocument();
  });
});
