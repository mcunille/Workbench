import {
  useCallback,
  useEffect,
  useMemo,
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

  const refresh = useCallback(async () => {
    try {
      const result = await getCurrentIdentity();
      setIdentity(result);
      setStatus(result ? 'signed-in' : 'signed-out');
    } catch (error) {
      setIdentity(null);
      setStatus(statusFor(error));
    }
  }, []);

  useEffect(() => {
    let current = true;
    void getCurrentIdentity().then(
      (result) => {
        if (current) {
          setIdentity(result);
          setStatus(result ? 'signed-in' : 'signed-out');
        }
      },
      (error: unknown) => {
        if (current) {
          setStatus(statusFor(error));
        }
      },
    );
    return () => {
      current = false;
    };
  }, []);

  const signIn = useCallback(async (email: string, password: string) => {
    await signInRequest(email, password);
    const result = await getCurrentIdentity();
    if (!result) {
      throw new ApiError(401);
    }
    setIdentity(result);
    setStatus('signed-in');
  }, []);

  const signOut = useCallback(async () => {
    await signOutRequest();
    setIdentity(null);
    setStatus('signed-out');
  }, []);

  const value = useMemo(
    () => ({ identity, status, refresh, signIn, signOut }),
    [identity, refresh, signIn, signOut, status],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
