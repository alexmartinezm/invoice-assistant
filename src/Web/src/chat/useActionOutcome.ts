import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, api } from '../api/client';
import type { ActionOutcome, ExecutionStatus } from '../api/types';
import type { ApprovalState } from './ApprovalCard';

/** Stop watching after this. A card that polls for ever is a tab that never goes quiet. */
export const watchWindowMs = 90_000;

/** Often enough to feel live, rarely enough that a queued delivery is not a load generator. */
export const pollIntervalMs = 2_000;

const unsettled: ReadonlySet<ExecutionStatus> = new Set(['pending', 'executing', 'unknown']);

/**
 * Turns an approval outcome into the state a card renders, and keeps watching while the answer is
 * still owed.
 *
 * Approving used to be the end of the story: one request, one sentence, done. It is not any more —
 * a write that hands off to a delivery settles minutes later — so a card that showed the first
 * answer and stopped would be showing a guess. Every sentence it displays is the server's; this
 * hook decides when to ask again, never what to say.
 *
 * That last part used to be aspirational: the closing line for an execution that settled while the
 * card was watching was rebuilt here from the status, which meant the client was quietly asserting
 * "nothing changed" for a delivery the provider refused after the invoice had been issued. The
 * execution now carries the server's sentence, and this renders it.
 */
export function useActionOutcome(token: string) {
  const timers = useRef(new Map<string, number>());

  const [, force] = useState(0);
  const rerender = useCallback(() => force((tick) => tick + 1), []);

  const stopAll = useCallback(() => {
    for (const timer of timers.current.values()) {
      window.clearTimeout(timer);
    }

    timers.current.clear();
  }, []);

  useEffect(() => stopAll, [stopAll]);

  /**
   * The state an outcome puts a card into, plus the polling that keeps it honest.
   *
   * `onState` is called again whenever the execution moves, so the caller does not have to hold a
   * second copy of the answer.
   */
  const track = useCallback(
    (actionId: string, outcome: ActionOutcome, onState: (state: ApprovalState) => void) => {
      onState(toState(outcome));

      const executionId = outcome.executionId;
      if (executionId === null || !unsettled.has(outcome.executionStatus ?? 'succeeded')) {
        return;
      }

      const deadline = Date.now() + watchWindowMs;

      const poll = async () => {
        try {
          const execution = await api.actionExecution(token, executionId);

          if (!unsettled.has(execution.status)) {
            onState({
              status: 'resolved',
              message: execution.message ?? outcome.message,
              failed: execution.status === 'failed',
            });
            timers.current.delete(actionId);
            rerender();
            return;
          }

          onState({
            status: 'running',
            executionId,
            execution: execution.status,
            message: execution.message ?? outcome.message,
          });
        } catch (cause) {
          // A 404 means it is not ours to watch; anything else may well succeed next time. Neither
          // is worth a red card in front of a decision the user already made.
          if (cause instanceof ApiError && cause.status === 404) {
            timers.current.delete(actionId);
            return;
          }
        }

        if (Date.now() < deadline) {
          timers.current.set(
            actionId,
            window.setTimeout(() => void poll(), pollIntervalMs),
          );
        } else {
          timers.current.delete(actionId);
        }
      };

      timers.current.set(
        actionId,
        window.setTimeout(() => void poll(), pollIntervalMs),
      );
    },
    [rerender, token],
  );

  return { track, stopAll };
}

/** The first answer, straight from the approval response. */
export function toState(outcome: ActionOutcome): ApprovalState {
  if (outcome.executionId === null || !unsettled.has(outcome.executionStatus ?? 'succeeded')) {
    return {
      status: 'resolved',
      message: outcome.message,
      failed: outcome.executionStatus === 'failed',
    };
  }

  return {
    status: 'running',
    executionId: outcome.executionId,
    execution: outcome.executionStatus ?? 'executing',
    message: outcome.message,
  };
}
