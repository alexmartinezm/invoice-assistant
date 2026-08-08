import { useEffect, useState } from 'react';
import type { ExecutionStatus } from '../api/types';

export type ApprovalState =
  | { status: 'pending' }
  | { status: 'working'; decision: 'approve' | 'reject' }
  /** Approved, and the outcome is still being established. The card keeps watching. */
  | { status: 'running'; executionId: string; execution: ExecutionStatus; message: string }
  | { status: 'resolved'; message: string; failed: boolean };

export interface PendingApproval {
  actionId: string;
  tool: string;
  summary: string;
  expiresAt: string;
  /** Whether this user clears the tool's role floor. The server decides; the card only renders it. */
  canApprove: boolean;
  /** The role the tool needs, so the card can name who to escalate to. */
  requiredRole: string;
  /** False when somebody else proposed it and it was escalated to this user. */
  mine?: boolean;
  state: ApprovalState;
}

/**
 * The decision point: a write the assistant proposed and cannot make on its own.
 *
 * Structured like `InvoiceDetailPanel` — a header saying what kind of thing this is, a body of
 * facts, a decision at the end — because that is the shape this app already uses for "here is a
 * record, act on it". It carries the accent rather than the aging ramp: pending is not late, and
 * amber-through-red in this app means one thing only.
 *
 * The summary is rendered as plain text. It is written by the server from resolved data, and
 * treating it as markup would put the one sentence the user is agreeing to at the mercy of whatever
 * ends up inside an invoice description.
 */
export function ApprovalCard({
  approval,
  onDecide,
}: {
  approval: PendingApproval;
  onDecide: (actionId: string, decision: 'approve' | 'reject') => void;
}) {
  const remaining = useCountdown(approval.expiresAt, approval.state.status === 'pending');
  const expired = remaining <= 0;

  if (approval.state.status === 'resolved') {
    return <ResolvedNote message={approval.state.message} failed={approval.state.failed} />;
  }

  if (approval.state.status === 'running') {
    return <ExecutionNote state={approval.state} />;
  }

  const inFlight = approval.state.status === 'working' ? approval.state.decision : null;

  return (
    <section
      aria-label="Approval required"
      className="slide-in border-accent bg-raised overflow-hidden rounded-lg border"
    >
      <header className="border-rule bg-accent-soft text-accent-ink flex items-baseline gap-2 border-b px-3 py-2">
        <h3 className="flex-1 text-xs font-semibold tracking-wide uppercase">
          {approval.canApprove ? 'Approval required' : `Needs an ${approval.requiredRole}`}
        </h3>
        <span className="numeric text-[11px]" aria-live="off">
          {expired ? 'expired' : formatRemaining(remaining)}
        </span>
      </header>

      <p className="px-3 py-3 text-sm leading-relaxed break-words">{approval.summary}</p>

      <p className="text-ink-faint px-3 pb-3 font-mono text-[11px]">
        {approval.tool}
        {approval.mine === false && ' · proposed by someone else'}
      </p>

      {/* Wraps rather than squeezing: at 320px the expiry hint sits beside the buttons only if
          there is room for it, and a shrunken "Approve" is worse than a second line. */}
      <div className="border-rule flex flex-wrap items-center gap-2 border-t px-3 py-2">
        {/* No Approve button below the tool's role floor. The server would refuse the click, and a
            button whose only outcome is a 403 is worse than no button: it reads as a decision the
            user can make, spends the expiry window finding out otherwise, and teaches them to
            distrust the ones that do work. Reject stays — withdrawing your own proposal is
            always yours to do. */}
        {approval.canApprove ? (
          <button
            type="button"
            onClick={() => onDecide(approval.actionId, 'approve')}
            disabled={inFlight !== null || expired}
            className="bg-accent text-paper hover:bg-accent-ink rounded-md px-3 py-1.5 text-sm font-medium transition-colors disabled:opacity-40"
          >
            {inFlight === 'approve' ? 'Approving…' : 'Approve'}
          </button>
        ) : (
          <p className="text-ink-soft text-xs">
            An {approval.requiredRole} has to approve this one.
          </p>
        )}

        <button
          type="button"
          onClick={() => onDecide(approval.actionId, 'reject')}
          disabled={inFlight !== null || expired}
          className="border-rule hover:bg-sunken ml-auto rounded-md border px-3 py-1.5 text-sm transition-colors disabled:opacity-40"
        >
          {inFlight === 'reject' ? 'Rejecting…' : approval.canApprove ? 'Reject' : 'Withdraw'}
        </button>

        {expired && (
          <p className="text-ink-faint w-full text-xs">Ask again to propose it afresh.</p>
        )}
      </div>
    </section>
  );
}

/**
 * Approved, and not yet finished.
 *
 * This is the state the card never used to have: it went from "Approve" straight to "Done", which
 * was a claim the server was in no position to make about a delivery nobody had confirmed. The
 * heading says which of the two unfinished things is happening, because "still going" and "we do
 * not know" are different news.
 */
function ExecutionNote({ state }: { state: Extract<ApprovalState, { status: 'running' }> }) {
  const unknown = state.execution === 'unknown';

  return (
    <section
      aria-label="Approved, awaiting the outcome"
      aria-busy="true"
      className="border-rule bg-raised overflow-hidden rounded-lg border"
      role="status"
    >
      <header className="border-rule bg-sunken text-ink-soft flex items-center gap-2 border-b px-3 py-2">
        {/* Neutral while running, exactly like a tool chip: work in flight is not a warning, and the
            aging ramp means one thing in this app. The two unfinished states are told apart by the
            heading rather than by the dot, because animation alone disappears under
            prefers-reduced-motion. */}
        <span
          aria-hidden="true"
          className={`h-1.5 w-1.5 rounded-full ${unknown ? 'bg-ink-soft' : 'bg-ink-faint animate-pulse'}`}
        />
        <h3 className="flex-1 text-xs font-semibold tracking-wide uppercase">
          {unknown ? 'Outcome unknown · reconciling' : 'Approved · executing'}
        </h3>
      </header>

      <p className="px-3 py-3 text-sm leading-relaxed break-words">{state.message}</p>

      <p className="text-ink-faint numeric px-3 pb-3 text-[11px] break-all">
        execution {state.executionId}
      </p>
    </section>
  );
}

/** What happened, in the server's words. Not a card any more: the decision is behind us. */
function ResolvedNote({ message, failed }: { message: string; failed: boolean }) {
  return (
    <p
      role="status"
      className={`rounded-md border px-3 py-2 text-sm break-words ${
        failed ? 'border-danger text-danger' : 'border-rule text-ink-soft'
      }`}
    >
      {message}
    </p>
  );
}

/**
 * Seconds left, ticking. An approval the user cannot see expiring is a trap: they read the card,
 * think about it, click, and get a conflict with no explanation.
 */
function useCountdown(expiresAt: string, running: boolean): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!running) return;

    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);

  return Math.max(0, Math.round((new Date(expiresAt).getTime() - now) / 1000));
}

function formatRemaining(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
}
