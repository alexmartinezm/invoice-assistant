import { useCallback, useEffect, useRef, useState } from 'react';
import { streamChatTurn } from '../api/client';

export interface ToolRun {
  tool: string;
  label: string;
  finished: boolean;
}

export type ChatItem =
  | { kind: 'user'; text: string }
  | { kind: 'assistant'; text: string; tools: ToolRun[]; failure: string | null };

/**
 * Drives one conversation. The SSE events map straight onto what the user sees: `activity` becomes
 * a tool chip, `token` appends to the answer, `error` becomes a red card, and `conversation`
 * carries the trace id shown in the footer.
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

      const updated = { ...last, tools: [...last.tools] };
      change(updated);
      return [...current.slice(0, -1), updated];
    });
  }, []);

  const send = useCallback(
    async (message: string) => {
      const text = message.trim();
      if (!text || streaming) return;

      abort.current?.abort();
      abort.current = new AbortController();

      setItems((current) => [
        ...current,
        { kind: 'user', text },
        { kind: 'assistant', text: '', tools: [], failure: null },
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

  const reset = useCallback(() => {
    abort.current?.abort();
    setItems([]);
    setConversationId(null);
    setTraceId(null);
    setStreaming(false);
  }, []);

  return { items, streaming, traceId, send, reset };
}
