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
  const transitionAccessCleared = useRef(false);

  const refresh = useCallback(async (mode?: 'permissions') => {
    // Access-loss checks discard protected state immediately. A successful role
    // change only refreshes permission claims, preserving the saved editor feedback.
    if (mode !== 'permissions') {
      setIdentity(null);
      setStatus('loading');
    }
    // Neither refresh mode may replace an in-flight login/logout. Access-loss
    // checks still clear protected UI above while that transition completes.
    if (authenticationTransition.current !== null) {
      if (mode !== 'permissions') transitionAccessCleared.current = true;
      return;
    }
    const generation = ++operationGeneration.current;
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
      if (generation === operationGeneration.current) {
        if (transitionAccessCleared.current) {
          setIdentity(null);
          setStatus(statusFor(error));
        }
        throw error;
      }
    } finally {
      if (authenticationTransition.current === generation) {
        authenticationTransition.current = null;
        transitionAccessCleared.current = false;
      }
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
      if (generation === operationGeneration.current) {
        if (transitionAccessCleared.current) {
          setIdentity(null);
          setStatus(statusFor(error));
        }
        throw error;
      }
    } finally {
      if (authenticationTransition.current === generation) {
        authenticationTransition.current = null;
        transitionAccessCleared.current = false;
      }
    }
  }, []);

  const value = useMemo(
    () => ({ identity, status, refresh, signIn, signOut }),
    [identity, refresh, signIn, signOut, status],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

