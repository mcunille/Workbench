import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type MouseEvent,
} from 'react';
export function useNavigation() {
  const [path, setPath] = useState(window.location.pathname);
  const [confirmation, setConfirmation] = useState(false);
  const dirty = useRef(false);
  const uncertain = useRef(false);
  const [uncertainConfirmation, setUncertainConfirmation] = useState(false);
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
    (next: string) => {
      request(() => {
        dirty.current = false;
        uncertain.current = false;
        index.current += 1;
        window.history.pushState({ workbenchIndex: index.current }, '', next);
        setPath(next);
      });
    },
    [request],
  );
  const setDirty = useCallback((value: boolean, unknownSave: boolean) => {
    dirty.current = value;
    uncertain.current = unknownSave;
  }, []);
  useEffect(() => {
    window.history.replaceState(
      { ...window.history.state, workbenchIndex: index.current },
      '',
    );
    const pop = (event: PopStateEvent) => {
      if (ignorePop.current) {
        ignorePop.current = false;
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
    navigate,
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
