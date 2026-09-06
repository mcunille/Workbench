import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

// Capture outside React: StrictMode may replay component initializers.
let recoveryToken: string | null = null;
if (window.location.pathname === '/recover' || window.location.pathname === '/invite') {
  const query = new URLSearchParams(window.location.search);
  const fragment = new URLSearchParams(window.location.hash.slice(1));
  recoveryToken = fragment.get('token') ?? query.get('token');
  if (query.has('token') || fragment.has('token')) {
    query.delete('token');
    const search = query.toString();
    let hash = window.location.hash;
    if (fragment.has('token')) {
      fragment.delete('token');
      const remainingFragment = fragment.toString();
      hash = remainingFragment ? `#${remainingFragment}` : '';
    }
    window.history.replaceState(
      window.history.state,
      '',
      window.location.pathname + (search ? `?${search}` : '') + hash,
    );
  }
}

const root = document.getElementById('root');

if (!root) {
  throw new Error('The Workbench application root is missing.');
}

createRoot(root).render(
  <StrictMode>
    <App recoveryToken={recoveryToken} />
  </StrictMode>,
);
