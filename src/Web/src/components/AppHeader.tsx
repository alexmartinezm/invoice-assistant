import { useState } from 'react';
import { Link, NavLink, useLocation } from 'react-router-dom';
import { RoleBadge } from './Pills';
import { useSession } from '../auth/useAuth';

const pages = [
  { to: '/invoices', label: 'receivables' },
  { to: '/usage', label: 'usage' },
];

export function AppHeader({ onOpenChat }: { onOpenChat: () => void }) {
  const { session, signOut } = useSession();
  const [showToken, setShowToken] = useState(false);

  return (
    <header className="border-rule bg-paper/85 sticky top-0 z-20 border-b backdrop-blur">
      <div className="mx-auto flex max-w-7xl items-center gap-2 px-4 py-3 sm:gap-4 sm:px-6">
        {/* The wordmark is the element that yields when the row is tight — the role badge and the
            drawer trigger are functional, a logo is not. Below `sm` it goes entirely rather than
            truncating: at 375px it rendered as a nine-pixel sliver of itself, which reads as a
            rendering fault rather than as restraint. */}
        <span className="font-display hidden min-w-0 truncate text-base tracking-tight sm:block sm:text-lg">
          invoice-assistant
        </span>

        <PageNav />

        <div className="flex-1" />

        {/* Short enough to stay on one line at 320px: a wrapped CTA label reads as broken. It
            steps down a size on the narrowest phones so the role badge — `ACCOUNTANT` is the wide
            one — keeps its place in the row. */}
        <button
          type="button"
          onClick={onOpenChat}
          className="border-accent text-accent-ink bg-accent-soft hover:bg-accent hover:text-paper shrink-0 rounded-md border px-2.5 py-1.5 text-xs font-medium whitespace-nowrap transition-colors sm:px-3 sm:text-sm lg:hidden"
        >
          Assistant
        </button>

        {/*
          The token is on display on purpose: it is the thing the assistant's tools carry, and
          seeing the role claim inside it is what makes "propagated identity" concrete.
        */}
        <div className="relative shrink-0">
          <button
            type="button"
            onClick={() => setShowToken((open) => !open)}
            aria-expanded={showToken}
            className="hover:bg-sunken flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors"
          >
            {/* Below sm the badge alone identifies the session; the name would push the role
                past the viewport edge, and the role is the part that matters here. */}
            <span className="hidden text-right text-sm leading-tight sm:block">
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

/**
 * The breadcrumb grew a second page and became the navigation, in the same register it always had:
 * path-like, monospace, quiet, with the active page inked and the other faint.
 *
 * Below `sm` it collapses to a link to the *other* page rather than listing both. Two full labels,
 * the drawer trigger and the role badge do not fit at 320px — and with `ACCOUNTANT` in the badge
 * they overflow by a wide margin, which is the case that matters because the role is the thing this
 * header exists to show. A menu would be ceremony for one destination; a switch is what two pages
 * actually need.
 */
function PageNav() {
  const { pathname } = useLocation();
  const other = pages.find((page) => !pathname.startsWith(page.to)) ?? pages[1]!;

  return (
    <nav aria-label="Pages" className="font-mono text-xs">
      <Link
        to={other.to}
        className="text-ink-faint hover:text-ink whitespace-nowrap transition-colors sm:hidden"
      >
        → {other.label}
      </Link>

      <span className="hidden items-center gap-3 sm:flex">
        {pages.map((page) => (
          <PageLink key={page.to} to={page.to} label={page.label} />
        ))}
      </span>
    </nav>
  );
}

function PageLink({ to, label }: { to: string; label: string }) {
  return (
    <NavLink
      to={to}
      className={({ isActive }) =>
        `whitespace-nowrap transition-colors ${
          isActive
            ? 'text-ink decoration-accent underline underline-offset-4'
            : 'text-ink-faint hover:text-ink'
        }`
      }
    >
      / {label}
    </NavLink>
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
