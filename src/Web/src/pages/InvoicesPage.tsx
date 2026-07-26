import { useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '../api/client';
import type {
  AgingBucketKey,
  InvoiceDetail,
  InvoiceFilters,
  InvoiceSummary,
  ReceivablesReport,
} from '../api/types';
import { useSession } from '../auth/useAuth';
import { InvoiceDetailPanel } from '../invoices/InvoiceDetailPanel';
import { InvoiceFilterBar } from '../invoices/InvoiceFilterBar';
import { InvoiceTable } from '../invoices/InvoiceTable';
import { ReceivablesStrip } from '../invoices/ReceivablesStrip';

/** Bucket boundaries in days overdue, matching the server's aging report. */
const bucketRanges: Record<AgingBucketKey, [number, number]> = {
  current: [0, 0],
  '1-30': [1, 30],
  '31-60': [31, 60],
  '60+': [61, Number.MAX_SAFE_INTEGER],
};

export function InvoicesPage() {
  const { session } = useSession();
  const token = session.accessToken;

  const [filters, setFilters] = useState<InvoiceFilters>({ status: '' });
  const [bucket, setBucket] = useState<AgingBucketKey | null>(null);
  const [invoices, setInvoices] = useState<InvoiceSummary[]>([]);
  const [report, setReport] = useState<ReceivablesReport | null>(null);
  const [selected, setSelected] = useState<InvoiceDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);

      try {
        const [list, receivables] = await Promise.all([
          api.invoices(token, filters),
          api.receivables(token),
        ]);

        if (cancelled) return;
        setInvoices(list.invoices);
        setReport(receivables);
        setError(null);
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof ApiError ? cause.message : 'Could not load the ledger.');
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void load();
    return () => {
      cancelled = true;
    };
  }, [filters, token]);

  // Bucket selection narrows the rows using daysOverdue, which the server computed and sent —
  // the browser groups, it does not recalculate.
  const visible = useMemo(() => {
    if (!bucket) return invoices;

    const [from, to] = bucketRanges[bucket];
    return invoices.filter(
      (invoice) =>
        invoice.status === 'Sent' && invoice.daysOverdue >= from && invoice.daysOverdue <= to,
    );
  }, [bucket, invoices]);

  async function openInvoice(number: string) {
    if (selected?.number === number) {
      setSelected(null);
      return;
    }

    try {
      setSelected(await api.invoice(token, number));
    } catch (cause) {
      setError(cause instanceof ApiError ? cause.message : `Could not open ${number}.`);
    }
  }

  return (
    <div className="mx-auto max-w-7xl space-y-6 px-6 py-8">
      {error && (
        <p
          role="alert"
          className="border-aging-over text-aging-over rounded-md border px-4 py-3 text-sm"
        >
          {error}
        </p>
      )}

      {report && <ReceivablesStrip report={report} selected={bucket} onSelect={setBucket} />}

      {/* Above the table, not below it: with forty rows on screen, a detail panel appended at the
          bottom of the page is a detail panel nobody sees. */}
      {selected && <InvoiceDetailPanel invoice={selected} onClose={() => setSelected(null)} />}

      <div className="space-y-4">
        <InvoiceFilterBar filters={filters} onChange={setFilters} resultCount={visible.length} />

        <div aria-busy={loading} className={loading ? 'opacity-60 transition-opacity' : undefined}>
          <InvoiceTable
            invoices={visible}
            selectedNumber={selected?.number ?? null}
            onSelect={(number) => void openInvoice(number)}
          />
        </div>
      </div>
    </div>
  );
}
