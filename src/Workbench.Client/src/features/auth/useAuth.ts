import { createContext, useContext } from 'react';
import type { CurrentIdentity } from '../../api/auth';

export type AuthStatus = 'loading' | 'signed-out' | 'signed-in' | 'forbidden' | 'unavailable';

export interface AuthValue {
  identity: CurrentIdentity | null;
  status: AuthStatus;
  refresh(): Promise<void>;
  signIn(email: string, password: string): Promise<void>;
  signOut(): Promise<void>;
}

export const AuthContext = createContext<AuthValue | undefined>(undefined);

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) {
    throw new Error('useAuth must be used inside AuthProvider.');
  }
  return value;
}
