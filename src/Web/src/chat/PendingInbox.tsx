import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import type { PendingActionView } from '../api/types';
import { ApprovalCard, type PendingApproval } from './ApprovalCard';

/** How often the queue is re-read. Proposals lapse in five minutes, so this is frequent enough. */
const refreshMs = 30_000;

/**
 * The proposals waiting on this user that are not already cards in the transcript.
 *
 * It exists because escalation had nowhere to land. An Admin has always been allowed to resolve a
 * proposal somebody else made, but with no way to discover one and a five-minute window, that
 * permission was unreachable: an Accountant who proposed a cancellation could only watch their own
 * card expire. This is also what survives a page reload — before, a pending proposal vanished with
 * the transcript that carried it.
 *
 * Built from `ApprovalCard` rather than a list of its own: the decision is the same decision, and
 * inventing a second shape for it would be two vocabularies for one idea.
 */
export function PendingInbox({
  token,
  transcriptActionIds,
}: {
  token: string;
  /** Already on screen in the conversation below; showing them twice would be noise. */
  transcriptActionIds: ReadonlySet<string>;
}) {
  const [waiting, setWaiting] = useState<PendingActionView[]>([]);
  const [resolved, setResolved] = useState<Record<string, PendingApproval['state']>>({});

  const refresh = useCallback(async () => {
    try {
      setWaiting(await api.openActions(token));
    } catch {
      // A queue that cannot be read is not worth an error card in front of the chat: the
      // conversation still works, and the next poll may well succeed.
    }
  }, [token]);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), refreshMs);
    return () => window.clearInterval(timer);
  }, [refresh]);

  async function decide(actionId: string, decision: 'approve' | 'reject') {
    setResolved((current) => ({ ...current, [actionId]: { status: 'working', decision } }));

    try {
      const outcome = await api.resolveAction(token, actionId, decision);
      setResolved((current) => ({
        ...current,
        [actionId]: {
          status: 'resolved',
          message: outcome.message,
          failed: outcome.executionStatus === 'failed',
        },
      }));
    } catch (cause) {
      setResolved((current) => ({
        ...current,
        [actionId]: {
          status: 'resolved',
          message: cause instanceof Error ? cause.message : 'The action could not be resolved.',
          failed: true,
        },
      }));
    }

    void refresh();
  }

  const visible = waiting.filter((action) => !transcriptActionIds.has(action.actionId));
  if (visible.length === 0) return null;

  return (
    <section
      aria-label="Waiting for you"
      className="border-rule bg-sunken space-y-2 border-b px-4 py-3"
    >
      <h3 className="text-ink-faint font-mono text-[11px] tracking-wider uppercase">
        Waiting for you · {visible.length}
      </h3>

      {visible.map((action) => (
        <ApprovalCard
          key={action.actionId}
          approval={{
            actionId: action.actionId,
            tool: action.tool,
            summary: action.summary,
            expiresAt: action.expiresAt,
            canApprove: action.canApprove,
            requiredRole: action.requiredRole ?? 'Admin',
            mine: action.mine,
            state: resolved[action.actionId] ?? { status: 'pending' },
          }}
          onDecide={(id, decision) => void decide(id, decision)}
        />
      ))}
    </section>
  );
}
