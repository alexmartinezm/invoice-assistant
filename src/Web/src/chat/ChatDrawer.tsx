import { useEffect, useRef, useState } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useSession } from '../auth/useAuth';
import { useChatStream, type ChatItem, type ToolRun } from './useChatStream';

const suggestions = [
  'How much are we owed?',
  'Which invoices are overdue?',
  'What does Acme Ibérica owe us?',
];

export function ChatDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { session } = useSession();
  const { items, streaming, traceId, send, reset } = useChatStream(session.accessToken);
  const [draft, setDraft] = useState('');
  const transcript = useRef<HTMLDivElement>(null);

  useEffect(() => {
    transcript.current?.scrollTo({ top: transcript.current.scrollHeight, behavior: 'smooth' });
  }, [items]);

  async function submit(message: string) {
    setDraft('');
    await send(message);
  }

  return (
    <aside
      aria-label="Assistant"
      className={`bg-raised border-rule fixed inset-y-0 right-0 z-30 flex w-full max-w-md flex-col border-l shadow-card transition-transform duration-300 lg:sticky lg:top-0 lg:z-0 lg:h-dvh lg:max-w-none lg:translate-x-0 ${
        open ? 'translate-x-0' : 'translate-x-full'
      }`}
    >
      <header className="border-rule bg-sunken flex items-center gap-2 border-b px-4 py-3">
        <h2 className="font-display flex-1 text-base">Assistant</h2>

        <button
          type="button"
          onClick={reset}
          disabled={items.length === 0}
          className="text-ink-faint hover:text-ink text-xs disabled:opacity-40"
        >
          New conversation
        </button>

        <button
          type="button"
          onClick={onClose}
          aria-label="Close assistant"
          className="text-ink-faint hover:text-ink px-1 text-lg leading-none lg:hidden"
        >
          ×
        </button>
      </header>

      <div ref={transcript} className="flex-1 space-y-4 overflow-y-auto px-4 py-4">
        {items.length === 0 ? (
          <EmptyState onPick={submit} />
        ) : (
          items.map((item, index) => <Turn key={index} item={item} streaming={streaming} />)
        )}
      </div>

      <form
        onSubmit={(event) => {
          event.preventDefault();
          void submit(draft);
        }}
        className="border-rule border-t p-3"
      >
        <div className="flex items-end gap-2">
          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                void submit(draft);
              }
            }}
            rows={2}
            maxLength={2000}
            placeholder="Ask about invoices, customers or receivables…"
            className="border-rule bg-paper placeholder:text-ink-faint flex-1 resize-none rounded-md border px-3 py-2 text-sm"
          />

          <button
            type="submit"
            disabled={streaming || draft.trim().length === 0}
            className="bg-accent text-paper hover:bg-accent-ink rounded-md px-4 py-2 text-sm font-medium transition-colors disabled:opacity-40"
          >
            {streaming ? '…' : 'Send'}
          </button>
        </div>

        <p className="text-ink-faint mt-2 flex items-center justify-between gap-2 font-mono text-[11px]">
          <span>{session.user.role} · tools inherit this role</span>
          {traceId && <span title="OpenTelemetry trace id">trace {traceId.slice(0, 12)}</span>}
        </p>
      </form>
    </aside>
  );
}

function EmptyState({ onPick }: { onPick: (message: string) => void }) {
  return (
    <div className="pt-6">
      <p className="text-ink-soft text-sm leading-relaxed">
        Ask a question about the ledger. Every figure comes from a tool call against the API — the
        model never does the arithmetic.
      </p>

      <ul className="mt-4 space-y-2">
        {suggestions.map((suggestion) => (
          <li key={suggestion}>
            <button
              type="button"
              onClick={() => onPick(suggestion)}
              className="border-rule hover:bg-sunken hover:border-accent w-full rounded-md border px-3 py-2 text-left text-sm transition-colors"
            >
              {suggestion}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Turn({ item, streaming }: { item: ChatItem; streaming: boolean }) {
  if (item.kind === 'user') {
    return (
      <p className="bg-sunken border-rule ml-8 rounded-lg border px-3 py-2 text-sm">{item.text}</p>
    );
  }

  const awaitingFirstToken = streaming && item.text.length === 0 && !item.failure;

  return (
    <div className="space-y-2">
      {item.tools.map((run, index) => (
        <ToolChip key={`${run.tool}-${index}`} run={run} />
      ))}

      {item.text.length > 0 && (
        <div
          aria-live="polite"
          className="prose-sm max-w-none text-sm leading-relaxed [&_table]:w-full [&_table]:border-collapse [&_td]:border-b [&_td]:border-[var(--rule)] [&_td]:py-1 [&_td]:pr-3 [&_th]:border-b [&_th]:border-[var(--rule)] [&_th]:py-1 [&_th]:pr-3 [&_th]:text-left"
        >
          {/*
            The model's output is untrusted input. react-markdown renders markdown to React
            elements and does not pass raw HTML through, which is exactly the guarantee wanted here.
            remark-gfm is needed for tables: the system prompt asks for a table whenever a listing
            runs past three rows, and tables are a GFM extension, not plain CommonMark.
          */}
          <Markdown remarkPlugins={[remarkGfm]}>{item.text}</Markdown>
        </div>
      )}

      {awaitingFirstToken && <p className="caret text-ink-faint text-sm" />}

      {item.failure && (
        <p
          role="alert"
          className="border-aging-over text-aging-over rounded-md border px-3 py-2 text-sm"
        >
          {item.failure}
        </p>
      )}
    </div>
  );
}

/** "Totalling receivables…" — what the assistant is doing, while it does it. */
function ToolChip({ run }: { run: ToolRun }) {
  return (
    <p className="slide-in text-ink-soft flex items-center gap-2 font-mono text-xs">
      <span
        className={`h-1.5 w-1.5 rounded-full ${run.finished ? 'bg-accent' : 'bg-aging-30 animate-pulse'}`}
      />
      {run.label}
      <span className="text-ink-faint">{run.tool}</span>
    </p>
  );
}
