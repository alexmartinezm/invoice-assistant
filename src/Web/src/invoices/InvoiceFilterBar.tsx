import type { InvoiceFilters, InvoiceStatus } from '../api/types';

const statuses: (InvoiceStatus | '')[] = ['', 'Draft', 'Sent', 'Paid', 'Cancelled'];

export function InvoiceFilterBar({
  filters,
  onChange,
  resultCount,
  truncation,
}: {
  filters: InvoiceFilters;
  onChange: (filters: InvoiceFilters) => void;
  resultCount: number;
  /** How much the server held back, when it returned a page rather than the whole match. */
  truncation: { loaded: number; matched: number } | null;
}) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      {/* Statuses are the four persisted ones; "overdue" is a separate toggle because it is
          derived from the due date, not a state an invoice can be in. */}
      {/* Scrolls rather than clips: five statuses do not fit at 320px, and a filter you cannot
          reach is worse than one you have to swipe to. */}
      <div
        className="border-rule bg-raised flex max-w-full overflow-x-auto rounded-md border"
        role="group"
        aria-label="Filter by status"
      >
        {statuses.map((status) => (
          <button
            key={status || 'all'}
            type="button"
            onClick={() => onChange({ ...filters, status })}
            aria-pressed={filters.status === status}
            className={`border-rule shrink-0 px-3 py-1.5 text-sm whitespace-nowrap transition-colors not-first:border-l ${
              filters.status === status ? 'bg-accent text-paper' : 'hover:bg-sunken text-ink-soft'
            }`}
          >
            {status || 'All'}
          </button>
        ))}
      </div>

      <label className="border-rule bg-raised hover:bg-sunken flex cursor-pointer items-center gap-2 rounded-md border px-3 py-1.5 text-sm transition-colors">
        <input
          type="checkbox"
          checked={filters.overdue ?? false}
          onChange={(event) => onChange({ ...filters, overdue: event.target.checked })}
          className="accent-accent"
        />
        Overdue only
      </label>

      <input
        type="search"
        value={filters.customerName ?? ''}
        onChange={(event) => onChange({ ...filters, customerName: event.target.value })}
        placeholder="Customer…"
        aria-label="Filter by customer name"
        className="border-rule bg-raised placeholder:text-ink-faint min-w-40 flex-1 rounded-md border px-3 py-1.5 text-sm"
      />

      {/* Metadata, so it stays ink-faint: a capped page is not a failure and not an aging signal,
          and borrowing `danger` or the ramp for it would blunt what those two colours mean. */}
      <span className="text-ink-faint numeric text-xs">
        {resultCount} shown
        {truncation && (
          <span
            title={`The ledger returns at most ${truncation.loaded} invoices per request, so the filters above narrow those ${truncation.loaded}. Narrow them further to reach the rest.`}
          >
            {' · first '}
            {truncation.loaded} of {truncation.matched}
          </span>
        )}
      </span>
    </div>
  );
}
