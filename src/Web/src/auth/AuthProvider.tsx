import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import { AuthContext, type Session } from './AuthContext';

const StorageKey = 'invoice-assistant.session';

/**
 * Keeps the session in localStorage so a reload does not log you out mid-demo. A real product
 * would use an httpOnly refresh cookie; here the token is deliberately inspectable, because
 * showing what the assistant carries is half the point.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(readStoredSession);

  const signIn = useCallback(async (email: string, password: string) => {
    const response = await api.login(email, password);
    const next: Session = {
      accessToken: response.accessToken,
      expiresAt: response.expiresAt,
      user: response.user,
    };

    localStorage.setItem(StorageKey, JSON.stringify(next));
    setSession(next);
  }, []);

  const signOut = useCallback(() => {
    localStorage.removeItem(StorageKey);
    setSession(null);
  }, []);

  const value = useMemo(() => ({ session, signIn, signOut }), [session, signIn, signOut]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

function readStoredSession(): Session | null {
  const stored = localStorage.getItem(StorageKey);
  if (!stored) return null;

  try {
    const session = JSON.parse(stored) as Session;
    return new Date(session.expiresAt) > new Date() ? session : null;
  } catch {
    return null;
  }
}
