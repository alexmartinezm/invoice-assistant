import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, streamChatTurn } from '../api/client';
import type { PendingApproval } from './ApprovalCard';

export interface ToolRun {
  tool: string;
  label: string;
  finished: boolean;
}

export interface Block {
  tool: string;
  reason: string;
}

export type ChatItem =
  | { kind: 'user'; text: string }
  | {
      kind: 'assistant';
      text: string;
      tools: ToolRun[];
      approvals: PendingApproval[];
      blocks: Block[];
      failure: string | null;
    };

/**
 * Drives one conversation. The SSE events map straight onto what the user sees: `activity` becomes
 * a tool chip, `token` appends to the answer, `approval_required` becomes a decision card,
 * `blocked` becomes a refusal, `error` becomes a red card, and `conversation` carries the trace id
 * shown in the footer.
 */
export function useChatStream(token: string) {
  const [items, setItems] = useState<ChatItem[]>([]);
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [traceId, setTraceId] = useState<string | null>(null);
  const [streaming, setStreaming] = useState(false);
  const abort = useRef<AbortController | null>(null);

  useEffect(() => () => abort.current?.abort(), []);

  const updateAnswer = useCallback((change: (answer: ChatItem & { kind: 'assistant' }) => void) => {
    setItems((current) => {
      const last = current.at(-1);
      if (last?.kind !== 'assistant') return current;

      const updated = {
        ...last,
        tools: [...last.tools],
        approvals: [...last.approvals],
        blocks: [...last.blocks],
      };
      change(updated);
      return [...current.slice(0, -1), updated];
    });
  }, []);

  /** Finds a card wherever it is in the transcript: it may have scrolled several turns back. */
  const updateApproval = useCallback(
    (actionId: string, change: (approval: PendingApproval) => PendingApproval) => {
      setItems((current) =>
        current.map((item) => {
          if (item.kind !== 'assistant') return item;
          if (!item.approvals.some((approval) => approval.actionId === actionId)) return item;

          return {
            ...item,
            approvals: item.approvals.map((approval) =>
              approval.actionId === actionId ? change(approval) : approval,
            ),
          };
        }),
      );
    },
    [],
  );

  const send = useCallback(
    async (message: string) => {
      const text = message.trim();
      if (!text || streaming) return;

      abort.current?.abort();
      abort.current = new AbortController();

      setItems((current) => [
        ...current,
        { kind: 'user', text },
        { kind: 'assistant', text: '', tools: [], approvals: [], blocks: [], failure: null },
      ]);
      setStreaming(true);

      try {
        await streamChatTurn(
          token,
          { message: text, conversationId },
          (event) => {
            switch (event.type) {
              case 'conversation':
                setConversationId(event.conversationId);
                setTraceId(event.traceId);
                break;

              case 'activity':
                updateAnswer((answer) => {
                  if (event.phase === 'start') {
                    answer.tools.push({ tool: event.tool, label: event.label, finished: false });
                    return;
                  }

                  const running = answer.tools.findLast(
                    (run) => run.tool === event.tool && !run.finished,
                  );
                  if (running) running.finished = true;
                });
                break;

              case 'token':
                updateAnswer((answer) => {
                  answer.text += event.text;
                });
                break;

              case 'approval_required':
                updateAnswer((answer) => {
                  answer.approvals.push({
                    actionId: event.actionId,
                    tool: event.tool,
                    summary: event.summary,
                    expiresAt: event.expiresAt,
                    // The server says whether this user clears the tool's role floor, so the card
                    // knows on arrival instead of finding out from a 403 after the click.
                    canApprove: event.canApprove,
                    requiredRole: event.requiredRole,
                    mine: true,
                    state: { status: 'pending' },
                  });
                });
                break;

              case 'blocked':
                updateAnswer((answer) => {
                  answer.blocks.push({ tool: event.tool, reason: event.reason });
                });
                break;

              case 'error':
                updateAnswer((answer) => {
                  answer.failure = event.message;
                });
                break;

              case 'done':
                setTraceId(event.traceId);
                break;
            }
          },
          abort.current.signal,
        );
      } catch (cause) {
        if (!abort.current.signal.aborted) {
          updateAnswer((answer) => {
            answer.failure = cause instanceof Error ? cause.message : 'The turn failed.';
          });
        }
      } finally {
        setStreaming(false);
      }
    },
    [conversationId, streaming, token, updateAnswer],
  );

  /**
   * Approving or rejecting is its own request, not a chat turn: the write runs under this user's
   * token and the outcome sentence comes back from the server, so the assistant never gets to
   * narrate what happened to somebody's money.
   */
  const decide = useCallback(
    async (actionId: string, decision: 'approve' | 'reject') => {
      updateApproval(actionId, (approval) => ({
        ...approval,
        state: { status: 'working', decision },
      }));

      try {
        const outcome = await api.resolveAction(token, actionId, decision);

        updateApproval(actionId, (approval) => ({
          ...approval,
          state: {
            status: 'resolved',
            message: outcome.message,
            failed: outcome.executionStatus === 'failed',
          },
        }));
      } catch (cause) {
        updateApproval(actionId, (approval) => ({
          ...approval,
          state: {
            status: 'resolved',
            message: cause instanceof Error ? cause.message : 'The action could not be resolved.',
            failed: true,
          },
        }));
      }
    },
    [token, updateApproval],
  );

  const reset = useCallback(() => {
    abort.current?.abort();
    setItems([]);
    setConversationId(null);
    setTraceId(null);
    setStreaming(false);
  }, []);

  /** Action ids already on screen as cards, so the pending queue does not show them twice. */
  const transcriptActionIds = useMemo(
    () =>
      new Set(
        items.flatMap((item) =>
          item.kind === 'assistant' ? item.approvals.map((approval) => approval.actionId) : [],
        ),
      ),
    [items],
  );

  return { items, streaming, traceId, transcriptActionIds, send, decide, reset };
}
