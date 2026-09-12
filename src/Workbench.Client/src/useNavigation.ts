import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type MouseEvent,
} from 'react';
export function useNavigation() {
  const [path, setPath] = useState(window.location.pathname);
  const currentPath = useRef(window.location.pathname);
  const [entryId, setEntryId] = useState<string>(
    () => window.history.state?.workbenchEntryId ?? crypto.randomUUID(),
  );
  const currentEntryId = useRef(entryId);
  // A view owns in-memory edits. Moving between history entries for the same
  // path changes origin metadata, but must not replace that mounted view.
  const [viewId, setViewId] = useState(entryId);
  const [confirmation, setConfirmation] = useState(false);
  const dirty = useRef(false);
  const uncertain = useRef(false);
  const [uncertainConfirmation, setUncertainConfirmation] =
    useState(false);
  const action = useRef<(() => void) | null>(null);
  const index = useRef<number>(window.history.state?.workbenchIndex ?? 0);
  const ignorePop = useRef(false);
  const allowPop = useRef(false);
  const request = useCallback((next: () => void) => {
    if (dirty.current) {
      action.current = next;
      setUncertainConfirmation(uncertain.current);
      setConfirmation(true);
    } else next();
  }, []);
  const navigate = useCallback(
    (next: string, onNavigated?: (entryId: string) => void) => {
      request(() => {
        dirty.current = false;
        uncertain.current = false;
        index.current += 1;
        currentEntryId.current = crypto.randomUUID();
        setEntryId(currentEntryId.current);
        setViewId(currentEntryId.current);
        window.history.pushState(
          {
            workbenchIndex: index.current,
            workbenchEntryId: currentEntryId.current,
          },
          '',
          next,
        );
        currentPath.current = next;
        setPath(next);
        onNavigated?.(currentEntryId.current);
      });
    },
    [request],
  );
  const setDirty = useCallback((value: boolean, unknownSave: boolean) => {
    dirty.current = value;
    uncertain.current = unknownSave;
  }, []);
  // A confirmed creation adopts its server URL without navigating away from
  // the editor that owns the receipt and current-document recovery state.
  const replace = useCallback((next: string) => {
    window.history.replaceState(window.history.state, '', next);
    currentPath.current = next;
    setPath(next);
  }, []);
  useEffect(() => {
    // Collection restores its in-memory position after mounting the loaded rows.
    const previousScrollRestoration = window.history.scrollRestoration;
    window.history.scrollRestoration = 'manual';
    window.history.replaceState(
      {
        ...window.history.state,
        workbenchIndex: index.current,
        workbenchEntryId: currentEntryId.current,
      },
      '',
    );
    const pop = (event: PopStateEvent) => {
      if (ignorePop.current) {
        ignorePop.current = false;
        return;
      }
      // Same-path entries may have different collection origins. Publish their
      // identity without remounting the form or clearing its draft guard.
      // Tag native fragments so later Back/Forward distances include them.
      if (window.location.pathname === currentPath.current) {
        index.current = event.state?.workbenchIndex ?? index.current + 1;
        currentEntryId.current =
          event.state?.workbenchEntryId ?? currentEntryId.current;
        setEntryId(currentEntryId.current);
        if (
          event.state?.workbenchIndex === undefined ||
          event.state?.workbenchEntryId === undefined
        ) {
          window.history.replaceState(
            {
              ...event.state,
              workbenchIndex: index.current,
              workbenchEntryId: currentEntryId.current,
            },
            '',
          );
        }
        return;
      }
      const targetIndex = event.state?.workbenchIndex ?? 0;
      const delta = targetIndex - index.current;
      if (dirty.current && !allowPop.current && delta) {
        ignorePop.current = true;
        window.history.go(-delta);
        action.current = () => {
          allowPop.current = true;
          window.history.go(delta);
        };
        setUncertainConfirmation(uncertain.current);
        setConfirmation(true);
      } else {
        allowPop.current = false;
        dirty.current = false;
        uncertain.current = false;
        index.current = targetIndex;
        currentEntryId.current =
          event.state?.workbenchEntryId ?? crypto.randomUUID();
        setEntryId(currentEntryId.current);
        currentPath.current = window.location.pathname;
        setViewId(currentEntryId.current);
        setPath(window.location.pathname);
      }
    };
    const unload = (event: BeforeUnloadEvent) => {
      if (dirty.current) {
        event.preventDefault();
        event.returnValue = '';
      }
    };
    window.addEventListener('popstate', pop);
    window.addEventListener('beforeunload', unload);
    return () => {
      window.history.scrollRestoration = previousScrollRestoration;
      window.removeEventListener('popstate', pop);
      window.removeEventListener('beforeunload', unload);
    };
  }, []);
  const follow = (event: MouseEvent<HTMLAnchorElement>) => {
    if (
      event.button !== 0 ||
      event.metaKey ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey
    )
      return;
    event.preventDefault();
    navigate(event.currentTarget.pathname);
  };
  return {
    path,
    entryId,
    viewId,
    navigate,
    replace,
    follow,
    request,
    setDirty,
    confirmation,
    uncertain: uncertainConfirmation,
    keep: () => {
      action.current = null;
      setConfirmation(false);
    },
    discard: () => {
      setConfirmation(false);
      const next = action.current;
      action.current = null;
      next?.();
    },
  };
}
