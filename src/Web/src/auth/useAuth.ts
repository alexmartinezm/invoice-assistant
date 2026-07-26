import { useContext } from 'react';
import { AuthContext, type AuthState } from './AuthContext';

export function useAuth(): AuthState {
  const state = useContext(AuthContext);

  if (!state) {
    throw new Error('useAuth must be used inside an AuthProvider.');
  }

  return state;
}

/** The session, for the parts of the app that only render once someone is signed in. */
export function useSession() {
  const { session, signOut } = useAuth();

  if (!session) {
    throw new Error('useSession must be used inside an authenticated route.');
  }

  return { session, signOut };
}
