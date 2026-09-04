import { render, screen } from '@testing-library/react';
import { Recovery } from './Recovery';

describe('Recovery', () => {
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
