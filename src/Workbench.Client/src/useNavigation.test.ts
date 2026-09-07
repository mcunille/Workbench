import { act, renderHook } from '@testing-library/react';
import { vi } from 'vitest';
import { useNavigation } from './useNavigation';
describe('Draft navigation', () => {
  it('clears the guard after leaving a discarded draft', () => {
    // GIVEN a dirty draft WHEN discarding to another page
    const { result } = renderHook(useNavigation);
    act(() => result.current.setDirty(true, false));
    act(() => result.current.navigate('/inventory'));
    act(() => result.current.discard());
    // THEN subsequent navigation does not ask about the abandoned draft
    act(() => result.current.navigate('/inventory/new'));
    expect(result.current.confirmation).toBe(false);
    expect(result.current.path).toBe('/inventory/new');
  });
  it('keeps guarding a dirty draft after a confirmed action fails to leave', () => {
    // GIVEN a dirty draft and an action that leaves the page unchanged
    const { result } = renderHook(useNavigation);
    const failedAction = vi.fn();
    act(() => result.current.setDirty(true, false));
    // WHEN discarding for that action
    act(() => result.current.request(failedAction));
    expect(result.current.confirmation).toBe(true);
    act(() => result.current.discard());
    expect(failedAction).toHaveBeenCalledOnce();
    // THEN a subsequent navigation still protects the visible draft
    act(() => result.current.request(vi.fn()));
    expect(result.current.confirmation).toBe(true);
  });
  it('keeps the draft when navigation is canceled', () => {
    // GIVEN a dirty draft WHEN choosing Keep editing THEN navigation is not executed
    const { result } = renderHook(useNavigation);
    const leave = vi.fn();
    act(() => result.current.setDirty(true, true));
    act(() => result.current.request(leave));
    expect(result.current.uncertain).toBe(true);
    act(() => result.current.keep());
    expect(leave).not.toHaveBeenCalled();
    expect(result.current.confirmation).toBe(false);
  });
});
