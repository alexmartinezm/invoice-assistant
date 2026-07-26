import type { ConversationUsageDetail, GateDecision } from '../api/types';
import { formatCostEur, formatCount, formatDateTime, formatTime } from '../format';

/**
 * Decisions that executed a write carry the accent (something happened); refusals are danger
 * (the gate said no). The aging ramp stays out of this — it means "late money", nothing else.
 */
const decisionText: Record<GateDecision, string> = {
  auto: 'text-accent-ink',
  confirmed: 'text-accent-ink',
  denied: 'text-danger',
  blocked: 'text-danger',
};

type TimelineEntry =
  | { kind: 'call'; at: string; model: string; tokens: string; latencyMs: number; costEur: number }
  | { kind: 'tool'; at: string; tool: string; decision: GateDecision };

export function ConversationDetailPanel({
  detail,
  onClose,
}: {
  detail: ConversationUsageDetail;
  onClose: () => void;
}) {
  // The server sends model calls and gate decisions as two ordered lists; interleaving them by
  // timestamp is grouping, not arithmetic — the euros were computed on the server.
  const timeline: TimelineEntry[] = [
    ...detail.calls.map<TimelineEntry>((call) => ({
      kind: 'call',
      at: call.at,
      model: call.model,
      tokens: `${formatCount(call.promptTokens)} in / ${formatCount(call.completionTokens)} out`,
      latencyMs: call.latencyMs,
      costEur: call.costEur,
    })),
    ...detail.toolEvents.map<TimelineEntry>((event) => ({
      kind: 'tool',
      at: event.at,
      tool: event.tool,
      decision: event.decision,
    })),
    // Compared as instants, not as strings. `localeCompare` applies locale collation to the
    // punctuation, and it puts "…09:00:02+00:00" *after* "…09:00:02.4+00:00" — so a whole-second
    // tool decision would sort below the model call that followed it.
  ].sort((a, b) => new Date(a.at).getTime() - new Date(b.at).getTime());

  return (
    <section aria-labelledby="conversation-detail" className="card slide-in overflow-hidden">
      <header className="border-rule bg-sunken flex items-start gap-3 border-b px-5 py-4">
        <div className="min-w-0 flex-1">
          <h2 id="conversation-detail" className="font-display text-lg">
            Conversation cost
          </h2>
          {/* Wraps rather than truncates: at 375px the truncation ate the whole address and left
              an ellipsis, and whose conversation this is is half of what the line says. */}
          <p className="text-ink-soft mt-0.5 text-sm">
            {formatDateTime(detail.startedAt)} ·{' '}
            {/* break-words, not break-all: the address moves to its own line whole rather than
                being split down the middle as "ca / rlos@demo". */}
            <span className="font-mono break-words">{detail.userEmail}</span>
          </p>
        </div>
        <p className="numeric text-lg whitespace-nowrap">{formatCostEur(detail.costEur)}</p>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close conversation detail"
          className="text-ink-faint hover:text-ink -mt-1 -mr-1 px-1 text-lg leading-none"
        >
          ×
        </button>
      </header>

      <dl className="border-rule grid grid-cols-2 gap-y-2 border-b px-5 py-4 text-sm sm:grid-cols-4">
        <Field label="Model calls" value={`${detail.modelCalls}`} />
        <Field label="Tool calls" value={`${detail.toolCalls}`} />
        <Field
          label="Tokens in / out"
          value={`${formatCount(detail.promptTokens)} / ${formatCount(detail.completionTokens)}`}
        />
        <Field label="Prompt hash" value={detail.systemPromptHash.slice(0, 12)} />
      </dl>

      <ol className="px-5 py-3">
        {timeline.map((entry, index) => (
          <li
            key={`${entry.at}-${index}`}
            className="border-rule flex items-baseline gap-3 border-b py-2 text-sm last:border-b-0"
          >
            <span className="numeric text-ink-faint shrink-0 text-xs">{formatTime(entry.at)}</span>

            {/* The middle column wraps instead of truncating. Tokens and latency are the reason
                this timeline exists; clipping them to fit a phone would leave the row saying
                nothing but the time and the price. */}
            {entry.kind === 'call' ? (
              <>
                <span className="min-w-0 flex-1">
                  <span className="font-mono text-xs">{entry.model}</span>
                  <span className="text-ink-soft ml-2 text-xs">
                    {entry.tokens} · <span className="numeric">{entry.latencyMs}</span> ms
                  </span>
                </span>
                <span className="numeric shrink-0 text-right">{formatCostEur(entry.costEur)}</span>
              </>
            ) : (
              <span className="min-w-0 flex-1">
                <span className="font-mono text-xs">{entry.tool}</span>
                <span className={`ml-2 text-xs font-medium ${decisionText[entry.decision]}`}>
                  {entry.decision}
                </span>
              </span>
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-ink-faint font-mono text-[11px] tracking-wider uppercase">{label}</dt>
      <dd className="numeric mt-0.5">{value}</dd>
    </div>
  );
}
