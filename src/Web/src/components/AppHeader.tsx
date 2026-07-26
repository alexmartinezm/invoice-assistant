import { useState } from 'react';
import { RoleBadge } from './Pills';
import { useSession } from '../auth/useAuth';

export function AppHeader({ onOpenChat }: { onOpenChat: () => void }) {
  const { session, signOut } = useSession();
  const [showToken, setShowToken] = useState(false);

  return (
    <header className="border-rule bg-paper/85 sticky top-0 z-20 border-b backdrop-blur">
      <div className="mx-auto flex max-w-7xl items-center gap-4 px-6 py-3">
        <span className="font-display text-lg tracking-tight">invoice-assistant</span>
        <span className="text-ink-faint hidden font-mono text-xs sm:inline">/ receivables</span>

        <div className="flex-1" />

        <button
          type="button"
          onClick={onOpenChat}
          className="border-accent text-accent-ink bg-accent-soft hover:bg-accent hover:text-paper rounded-md border px-3 py-1.5 text-sm font-medium transition-colors lg:hidden"
        >
          Ask the assistant
        </button>

        {/*
          The token is on display on purpose: it is the thing the assistant's tools carry, and
          seeing the role claim inside it is what makes "propagated identity" concrete.
        */}
        <div className="relative">
          <button
            type="button"
            onClick={() => setShowToken((open) => !open)}
            aria-expanded={showToken}
            className="hover:bg-sunken flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors"
          >
            <span className="text-right text-sm leading-tight">
              <span className="block font-medium">{session.user.displayName}</span>
              <span className="text-ink-faint block font-mono text-[11px]">
                {session.user.email}
              </span>
            </span>
            <RoleBadge role={session.user.role} />
          </button>

          {showToken && <TokenCard token={session.accessToken} onSignOut={signOut} />}
        </div>
      </div>
    </header>
  );
}

function TokenCard({ token, onSignOut }: { token: string; onSignOut: () => void }) {
  const claims = decodeJwtPayload(token);

  return (
    <div className="card slide-in absolute right-0 z-30 mt-2 w-96 max-w-[calc(100vw-3rem)] p-4">
      <p className="text-ink-faint font-mono text-[11px] tracking-widest uppercase">Bearer token</p>

      <pre className="bg-sunken border-rule text-ink-soft mt-2 max-h-40 overflow-auto rounded border p-3 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap">
        {token}
      </pre>

      {claims && (
        <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 font-mono text-[11px]">
          {Object.entries(claims).map(([claim, value]) => (
            <div key={claim} className="col-span-2 grid grid-cols-subgrid">
              <dt className="text-ink-faint truncate">{shortClaimName(claim)}</dt>
              <dd className="truncate">{String(value)}</dd>
            </div>
          ))}
        </dl>
      )}

      <button
        type="button"
        onClick={onSignOut}
        className="border-rule-strong hover:bg-sunken mt-4 w-full rounded-md border px-3 py-1.5 text-sm transition-colors"
      >
        Sign out
      </button>
    </div>
  );
}

/** Reads the claims for display only — the server is the one that validates the signature. */
function decodeJwtPayload(token: string): Record<string, unknown> | null {
  const payload = token.split('.')[1];
  if (!payload) return null;

  try {
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return null;
  }
}

const shortClaimName = (claim: string) => claim.split('/').pop() ?? claim;
