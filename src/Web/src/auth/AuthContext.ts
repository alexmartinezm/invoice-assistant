import { createContext } from 'react';
import type { AuthenticatedUser } from '../api/types';

export interface Session {
  accessToken: string;
  expiresAt: string;
  user: AuthenticatedUser;
}

export interface AuthState {
  session: Session | null;
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => void;
}

export const AuthContext = createContext<AuthState | null>(null);
