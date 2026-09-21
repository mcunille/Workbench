import { render, screen } from '@testing-library/react';
import { SupplierProfileLink } from './SupplierProfileLink';

it.each(['javascript:alert(1)', 'data:text/html,test', '//example.test', 'https://user:password@example.test', 'https://example.test/a b', 'https://example.test/\u0001', 'https://example.test\\path', 'https://example.test/' + 'a'.repeat(2048)])('does not expose an unsafe profile destination: %s', value => {
  // GIVEN an unsafe saved value WHEN rendering THEN it cannot become a navigable link.
  render(<SupplierProfileLink label="Instagram" value={value} />);
  expect(screen.queryByRole('link')).not.toBeInTheDocument();
});

it.each(['http://x.com/example', 'https://instagram.com/example'])('opens a safe saved destination: %s', value => {
  // GIVEN a safe web profile WHEN rendering THEN its platform and tab behavior are explicit.
  render(<SupplierProfileLink label="Profile" value={value} />);
  expect(screen.getByRole('link', { name: 'Open Profile profile (new tab)' })).toHaveAttribute('href', value);
});
