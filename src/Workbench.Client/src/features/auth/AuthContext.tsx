import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import {
  ApiError,
  getCurrentIdentity,
  signIn as signInRequest,
  signOut as signOutRequest,
  type CurrentIdentity,
} from '../../api/auth';
import { AuthContext, type AuthStatus } from './useAuth';

function statusFor(error: unknown): AuthStatus {
  if (error instanceof ApiError && error.status === 403) {
    return 'forbidden';
  }
  return 'unavailable';
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [identity, setIdentity] = useState<CurrentIdentity | null>(null);
  const [status, setStatus] = useState<AuthStatus>('loading');
  const operationGeneration = useRef(0);
  const authenticationTransition = useRef<number | null>(null);

  const refresh = useCallback(async (mode?: 'permissions') => {
    // A role-save completion cannot supersede a login/logout already in flight.
    if (mode === 'permissions' && authenticationTransition.current !== null) return;
    const generation = ++operationGeneration.current;
    // Access-loss checks discard protected state immediately. A successful role
    // change only refreshes permission claims, preserving the saved editor feedback.
    if (mode !== 'permissions') {
      setIdentity(null);
      setStatus('loading');
    }
    try {
      const result = await getCurrentIdentity();
      if (generation !== operationGeneration.current) return;
      setIdentity(result);
      setStatus(result ? 'signed-in' : 'signed-out');
    } catch (error) {
      if (generation !== operationGeneration.current) return;
      setIdentity(null);
      setStatus(statusFor(error));
    }
  }, []);

  useEffect(() => {
    const generation = ++operationGeneration.current;
    let current = true;
    void getCurrentIdentity().then(
      (result) => {
        if (current && generation === operationGeneration.current) {
          setIdentity(result);
          setStatus(result ? 'signed-in' : 'signed-out');
        }
      },
      (error: unknown) => {
        if (current && generation === operationGeneration.current) {
          setStatus(statusFor(error));
        }
      },
    );
    return () => {
      current = false;
      operationGeneration.current++;
    };
  }, []);

  const signIn = useCallback(async (email: string, password: string) => {
    const generation = ++operationGeneration.current;
    authenticationTransition.current = generation;
    try {
      await signInRequest(email, password);
      if (generation !== operationGeneration.current) return;
      const result = await getCurrentIdentity();
      if (generation !== operationGeneration.current) return;
      if (!result) throw new ApiError(401);
      setIdentity(result);
      setStatus('signed-in');
    } catch (error) {
      if (generation === operationGeneration.current) throw error;
    } finally {
      if (authenticationTransition.current === generation) authenticationTransition.current = null;
    }
  }, []);

  const signOut = useCallback(async () => {
    const generation = ++operationGeneration.current;
    authenticationTransition.current = generation;
    try {
      await signOutRequest();
      if (generation !== operationGeneration.current) return;
      setIdentity(null);
      setStatus('signed-out');
    } catch (error) {
      if (generation === operationGeneration.current) throw error;
    } finally {
      if (authenticationTransition.current === generation) authenticationTransition.current = null;
    }
  }, []);

  const value = useMemo(
    () => ({ identity, status, refresh, signIn, signOut }),
    [identity, refresh, signIn, signOut, status],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

