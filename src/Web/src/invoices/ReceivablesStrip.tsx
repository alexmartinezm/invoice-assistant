import type { AgingBucketKey, ReceivablesReport } from '../api/types';
import { formatMoney, formatMoneyCompact } from '../format';

const bucketColor: Record<AgingBucketKey, string> = {
  current: 'bg-aging-current',
  '1-30': 'bg-aging-30',
  '31-60': 'bg-aging-60',
  '60+': 'bg-aging-over',
};

const bucketText: Record<AgingBucketKey, string> = {
  current: 'text-aging-current',
  '1-30': 'text-aging-30',
  '31-60': 'text-aging-60',
  '60+': 'text-aging-over',
};

/**
 * The aging report, straight from `GET /api/reports/receivables`. Every number here was totalled
 * by the server — the same endpoint the assistant's `get_receivables_summary` tool calls, so the
 * chat and this strip can never disagree.
 */
export function ReceivablesStrip({
  report,
  selected,
  onSelect,
}: {
  report: ReceivablesReport;
  selected: AgingBucketKey | null;
  onSelect: (bucket: AgingBucketKey | null) => void;
}) {
  const total = report.totalOutstanding || 1;

  return (
    <section aria-labelledby="receivables" className="card p-5 sm:p-6">
      <div className="flex flex-wrap items-baseline justify-between gap-x-6 gap-y-2">
        <h2 id="receivables" className="font-display text-lg">
          Outstanding receivables
        </h2>
        <p className="text-ink-faint font-mono text-xs">
          as of {report.asOf} · {report.invoiceCount} invoices
        </p>
      </div>

      <p className="numeric mt-3 text-4xl tracking-tight">{formatMoney(report.totalOutstanding)}</p>
      <p className="text-ink-soft mt-1 text-sm">
        of which <span className="numeric text-aging-over">{formatMoney(report.totalOverdue)}</span>{' '}
        is overdue
      </p>

      {/* Proportional bar: how the debt is distributed is the story, not the individual figures. */}
      <div
        className="bg-sunken border-rule mt-6 flex h-2.5 gap-0.5 overflow-hidden rounded-full border"
        role="presentation"
      >
        {report.buckets.map((bucket) => (
          // No transition on flex-grow: animating a layout property forces reflow every frame,
          // and the bar is read once rather than watched.
          <div
            key={bucket.key}
            className={bucketColor[bucket.key]}
            style={{ flexGrow: bucket.amount / total }}
          />
        ))}
      </div>

      <ul className="mt-4 grid gap-2 sm:grid-cols-4">
        {report.buckets.map((bucket) => {
          const isSelected = selected === bucket.key;

          return (
            <li key={bucket.key}>
              <button
                type="button"
                onClick={() => onSelect(isSelected ? null : bucket.key)}
                aria-pressed={isSelected}
                className={`hover:bg-sunken w-full rounded-md border px-3 py-2 text-left transition-colors ${
                  isSelected ? 'border-accent bg-accent-soft' : 'border-transparent'
                }`}
              >
                <span className="flex items-center gap-1.5">
                  <span className={`h-2 w-2 shrink-0 rounded-full ${bucketColor[bucket.key]}`} />
                  <span className="text-ink-soft truncate text-xs">{bucket.label}</span>
                </span>
                <span className={`numeric mt-1 block text-lg ${bucketText[bucket.key]}`}>
                  {formatMoneyCompact(bucket.amount)}
                </span>
                <span className="text-ink-faint text-xs">{bucket.invoiceCount} invoices</span>
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
