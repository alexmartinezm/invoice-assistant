import { useMemo, useState } from 'react';
import type { Role } from '../api/types';
import { RoleBadge } from '../components/Pills';
import { useAuth } from '../auth/useAuth';
import { appConfig, formatMoney } from '../format';

/**
 * The seeded demo users. The password is shared and public: this is a demo repo on purpose.
 * The Accountant's ceiling is quoted from the server's own limit rather than written into the
 * copy, so changing `Invoicing:AccountantMarkPaidLimit` cannot leave this screen lying.
 */
const demoUsers = (
  settlementLimit: string,
): { email: string; name: string; role: Role; can: string }[] => [
  { email: 'ana@demo', name: 'Ana Ferrer', role: 'Admin', can: 'Everything, including cancelling' },
  {
    email: 'carlos@demo',
    name: 'Carlos Ibáñez',
    role: 'Accountant',
    can: `Read, draft, send, settle up to ${settlementLimit}`,
  },
  { email: 'lucia@demo', name: 'Lucía Prat', role: 'Viewer', can: 'Read only' },
];

const DemoPassword = 'demo1234';

export function LoginPage() {
  const { signIn } = useAuth();
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const users = useMemo(() => demoUsers(formatMoney(appConfig().accountantMarkPaidLimit)), []);

  async function handleSignIn(email: string) {
    setPending(email);
    setError(null);

    try {
      await signIn(email, DemoPassword);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not sign in.');
      setPending(null);
    }
  }

  return (
    <main className="mx-auto grid min-h-dvh max-w-6xl items-center gap-16 px-6 py-16 lg:grid-cols-[1.1fr_1fr] lg:gap-24">
      <section className="max-w-xl">
        <p className="text-ink-faint font-mono text-xs tracking-[0.2em] uppercase">
          Production-grade AI assistant · .NET
        </p>

        <h1 className="font-display mt-6 text-5xl leading-[1.05] tracking-tight text-balance sm:text-6xl">
          The model orchestrates.
          <span className="text-ink-faint"> The server decides.</span>
        </h1>

        <p className="text-ink-soft mt-6 text-lg leading-relaxed">
          An invoicing assistant where every calculation and every security decision lives in
          deterministic server code. Sign in as one of three users and watch the assistant inherit
          exactly their permissions — no more.
        </p>

        <dl className="mt-10 grid gap-x-8 gap-y-5 sm:grid-cols-2">
          {[
            ['Propagated identity', "Tools call our own REST API with the user's bearer token"],
            ['Server-side write gate', 'Writes are proposed, then confirmed by a human'],
            ['Evals in CI', 'A prompt regression breaks the pipeline'],
            ['Cost and traces', 'Tokens, cost and latency per conversation'],
          ].map(([term, description], index) => (
            <div key={term} className="ledger-row" style={{ animationDelay: `${index * 70}ms` }}>
              <dt className="border-rule border-t pt-3 text-sm font-semibold">{term}</dt>
              <dd className="text-ink-soft mt-1 text-sm leading-relaxed">{description}</dd>
            </div>
          ))}
        </dl>
      </section>

      <section aria-labelledby="pick-a-user">
        <div className="card overflow-hidden">
          <header className="border-rule bg-sunken border-b px-6 py-4">
            <h2 id="pick-a-user" className="font-display text-xl">
              Pick a user
            </h2>
            <p className="text-ink-soft mt-1 text-sm">
              Same question, different answers. The role travels with every tool call.
            </p>
          </header>

          <ul>
            {users.map((user) => (
              <li key={user.email} className="border-rule border-b last:border-b-0">
                <button
                  type="button"
                  onClick={() => handleSignIn(user.email)}
                  disabled={pending !== null}
                  className="hover:bg-sunken flex w-full items-center gap-4 px-6 py-4 text-left transition-colors disabled:opacity-50"
                >
                  <span className="flex-1">
                    <span className="flex items-center gap-2">
                      <span className="font-medium">{user.name}</span>
                      <RoleBadge role={user.role} />
                    </span>
                    <span className="text-ink-soft mt-0.5 block text-sm">{user.can}</span>
                  </span>

                  <span className="numeric text-ink-faint text-xs">
                    {pending === user.email ? 'signing in…' : user.email}
                  </span>
                </button>
              </li>
            ))}
          </ul>

          <footer className="border-rule bg-sunken text-ink-faint border-t px-6 py-3 font-mono text-xs">
            password: {DemoPassword}
          </footer>
        </div>

        {error && (
          <p
            role="alert"
            className="text-danger border-danger mt-4 rounded-md border px-4 py-3 text-sm"
          >
            {error}
          </p>
        )}
      </section>
    </main>
  );
}
