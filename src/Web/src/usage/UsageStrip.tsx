import type { UsageSummary } from '../api/types';
import { formatCostEur, formatCount, pluralize } from '../format';

/**
 * The cost headline, shaped like the receivables strip: one big number, a proportional bar, and
 * the figures behind it. The bar here is today's spend against the daily kill-switch budget —
 * the distribution is the story again, this time of a wallet instead of a debt.
 */
export function UsageStrip({ summary }: { summary: UsageSummary }) {
  const budget = summary.dailyBudgetEur || 1;
  const spentShare = Math.min(summary.spentTodayEur / budget, 1);

  return (
    <section aria-labelledby="usage-summary" className="card p-5 sm:p-6">
      <div className="flex flex-wrap items-baseline justify-between gap-x-6 gap-y-2">
        <h2 id="usage-summary" className="font-display text-lg">
          Assistant spend
        </h2>
        <p className="text-ink-faint font-mono text-xs">
          {pluralize(summary.conversations, 'conversation')} ·{' '}
          {pluralize(summary.modelCalls, 'model call')}
        </p>
      </div>

      <p className="numeric mt-3 text-4xl tracking-tight">{formatCostEur(summary.costEur)}</p>
      <p className="text-ink-soft mt-1 text-sm">
        <span className="numeric">{formatCount(summary.promptTokens)}</span> tokens in ·{' '}
        <span className="numeric">{formatCount(summary.completionTokens)}</span> tokens out — every
        call priced by the server, never by the model
      </p>

      {/* Today against the kill switch. Danger is a refusal colour: it appears only when the
          switch has actually tripped, not as a gradient of worry on the way there. */}
      <div
        className="bg-sunken border-rule mt-6 flex h-2.5 overflow-hidden rounded-full border"
        role="presentation"
      >
        <div
          className={summary.budgetExhausted ? 'bg-danger' : 'bg-accent'}
          style={{ flexGrow: spentShare }}
        />
        <div style={{ flexGrow: 1 - spentShare }} />
      </div>

      <p className="text-ink-soft mt-2 text-xs">
        <span className="numeric">{formatCostEur(summary.spentTodayEur)}</span> of the{' '}
        <span className="numeric">{formatCostEur(summary.dailyBudgetEur)}</span> daily budget spent
        today{' '}
        {summary.budgetExhausted ? (
          <span className="text-danger font-medium">
            — the kill switch has paused the assistant until midnight UTC
          </span>
        ) : (
          <span className="text-ink-faint">
            — once spent, /api/chat answers 429 until midnight UTC
          </span>
        )}
      </p>
    </section>
  );
}
