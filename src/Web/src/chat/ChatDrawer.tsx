import { useEffect, useRef, useState } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useSession } from '../auth/useAuth';
import { ApprovalCard } from './ApprovalCard';
import { BlockCard } from './BlockCard';
import { PendingInbox } from './PendingInbox';
import { ToolCatalogPanel } from './ToolCatalogPanel';
import { useChatStream, type ChatItem, type ToolRun } from './useChatStream';

const suggestions = [
  'How much are we owed?',
  'Which invoices are overdue?',
  'Which customer owes us the most?',
];

export function ChatDrawer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { session } = useSession();
  const { items, streaming, traceId, transcriptActionIds, send, decide, reset } = useChatStream(
    session.accessToken,
  );
  const [draft, setDraft] = useState('');
  const [catalogOpen, setCatalogOpen] = useState(false);
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

      {/* Above the transcript, not inside it: a decision escalated to you is not part of the
          conversation you are having, and burying it under the scroll is how it expires unseen. */}
      <PendingInbox token={session.accessToken} transcriptActionIds={transcriptActionIds} />

      <div ref={transcript} className="flex-1 space-y-4 overflow-y-auto px-4 py-4">
        {items.length === 0 ? (
          <EmptyState onPick={submit} />
        ) : (
          items.map((item, index) => (
            <Turn key={index} item={item} streaming={streaming} onDecide={decide} />
          ))
        )}
      </div>

      {/* Between the transcript and the composer, like the inbox above it: the catalog is a fact
          about the assistant, not a message in the conversation. */}
      {catalogOpen && <ToolCatalogPanel token={session.accessToken} />}

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

        {/* Wraps rather than squeezing: at 320px the role line and a trace id do not fit together,
            and a clickable label reading on two lines looks like a bug rather than a control. */}
        <p className="text-ink-faint mt-2 flex flex-wrap items-center justify-between gap-x-2 gap-y-1 font-mono text-[11px]">
          {/* The line already made the claim; now it opens the evidence for it. */}
          <button
            type="button"
            onClick={() => setCatalogOpen((open) => !open)}
            aria-expanded={catalogOpen}
            className="hover:text-ink text-left whitespace-nowrap transition-colors"
          >
            {session.user.role} · tools inherit this role
          </button>

          {traceId && (
            <span title="OpenTelemetry trace id" className="whitespace-nowrap">
              trace {traceId.slice(0, 12)}
            </span>
          )}
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

function Turn({
  item,
  streaming,
  onDecide,
}: {
  item: ChatItem;
  streaming: boolean;
  onDecide: (actionId: string, decision: 'approve' | 'reject') => void;
}) {
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

      {/* After the answer, not before it: the assistant says what it is proposing, and the card
          is what the user acts on. */}
      {item.blocks.map((block, index) => (
        <BlockCard key={`${block.tool}-${index}`} tool={block.tool} reason={block.reason} />
      ))}

      {item.approvals.map((approval) => (
        <ApprovalCard key={approval.actionId} approval={approval} onDecide={onDecide} />
      ))}

      {item.failure && (
        <p role="alert" className="border-danger text-danger rounded-md border px-3 py-2 text-sm">
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
      {/*
        Neutral while running, accent once done. The pulse alone would not carry it: under
        prefers-reduced-motion it stops, so the two states have to differ in colour as well.
      */}
      <span
        className={`h-1.5 w-1.5 rounded-full ${
          run.finished ? 'bg-accent' : 'bg-ink-faint animate-pulse'
        }`}
      />
      {run.label}
      <span className="text-ink-faint">{run.tool}</span>
    </p>
  );
}
