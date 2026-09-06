import { vi } from 'vitest';
import type { ReactElement } from 'react';

const { render } = vi.hoisted(() => ({ render: vi.fn() }));
vi.mock('react-dom/client', () => ({ createRoot: () => ({ render }) }));
vi.mock('./App', () => ({ App: () => null }));

describe('recovery startup', () => {
  it.each(['/recover', '/invite'])('scrubs %s before rendering without adding history', async (path) => {
    // GIVEN a capability-bearing link and an application root
    window.history.replaceState({ navigation: 'preserved' }, '', `${path}?token=sentinel&token=other&next=help#section`);
    const length = window.history.length;
    document.body.innerHTML = '<div id="root"></div>';
    render.mockImplementation((element: ReactElement<{ children: ReactElement<{ recoveryToken: string | null }> }>) => {
      // THEN the first render sees a clean URL and no added history entry
      expect(window.location.href).toBe(`${window.location.origin}${path}?next=help#section`);
      expect(window.history.state).toEqual({ navigation: 'preserved' });
      expect(window.history.length).toBe(length);
      expect(element.props.children.props.recoveryToken).toBe('sentinel');
    });
    vi.resetModules();

    // WHEN the application starts
    await import('./main');
    expect(render).toHaveBeenCalled();
  });

  it.each([
    ['/recover?%74oken=a%2Bb%2Fc%3D', '/recover', 'a+b/c='],
    ['/invite?token=', '/invite', ''],
    ['/recover#token=fragment&token=duplicate&section=help', '/recover#section=help', 'fragment'],
    ['/invite?token=query&next=help#token=fragment', '/invite?next=help', 'fragment'],
    ['/invite#token=a%2Bb%2Fc%3D', '/invite', 'a+b/c='],
    ['/recover', '/recover', null],
    ['/?token=ordinary', '/?token=ordinary', null],
  ])('handles startup at %s', async (input, expected, token) => {
    // GIVEN an encoded, empty, absent, or unrelated query
    window.history.replaceState(null, '', input);
    document.body.innerHTML = '<div id="root"></div>';
    render.mockImplementation((element: ReactElement<{ children: ReactElement<{ recoveryToken: string | null }> }>) => {
      // THEN only recovery capabilities are removed, with decoded data retained in memory
      expect(window.location.pathname + window.location.search + window.location.hash).toBe(expected);
      expect(element.props.children.props.recoveryToken).toBe(token);
    });
    vi.resetModules();
    // WHEN startup runs
    await import('./main');
  });
});
