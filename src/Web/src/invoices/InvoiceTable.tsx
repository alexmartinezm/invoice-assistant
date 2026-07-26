import type { InvoiceSummary } from '../api/types';
import { StatusPill } from '../components/Pills';
import { formatDate, formatMoney } from '../format';

export function InvoiceTable({
  invoices,
  selectedNumber,
  onSelect,
}: {
  invoices: InvoiceSummary[];
  selectedNumber: string | null;
  onSelect: (number: string) => void;
}) {
  if (invoices.length === 0) {
    return (
      <p className="text-ink-soft border-rule rounded-lg border border-dashed px-6 py-12 text-center text-sm">
        No invoice matches these filters.
      </p>
    );
  }

  return (
    <div className="card overflow-hidden">
      <table className="w-full border-collapse text-sm">
        <caption className="sr-only">Invoices matching the current filters</caption>
        <thead>
          <tr className="border-rule bg-sunken text-ink-faint border-b text-left font-mono text-[11px] tracking-wider uppercase">
            <th scope="col" className="px-4 py-2.5 font-medium">
              Number
            </th>
            <th scope="col" className="px-4 py-2.5 font-medium">
              Customer
            </th>
            <th scope="col" className="px-4 py-2.5 font-medium">
              Status
            </th>
            <th scope="col" className="px-4 py-2.5 font-medium">
              Due
            </th>
            <th scope="col" className="px-4 py-2.5 text-right font-medium">
              Total
            </th>
          </tr>
        </thead>

        <tbody>
          {invoices.map((invoice, index) => (
            <tr
              key={invoice.number}
              onClick={() => onSelect(invoice.number)}
              className={`ledger-row border-rule hover:bg-sunken cursor-pointer border-b transition-colors last:border-b-0 ${
                selectedNumber === invoice.number ? 'bg-accent-soft' : ''
              }`}
              style={{ animationDelay: `${Math.min(index, 20) * 18}ms` }}
            >
              <td className="px-4 py-2.5">
                <button
                  type="button"
                  className="numeric text-accent-ink text-left"
                  aria-current={selectedNumber === invoice.number}
                >
                  {invoice.number}
                </button>
              </td>
              <td className="max-w-56 truncate px-4 py-2.5">{invoice.customerName}</td>
              <td className="px-4 py-2.5">
                <StatusPill status={invoice.status} isOverdue={invoice.isOverdue} />
              </td>
              <td className="numeric text-ink-soft px-4 py-2.5">
                {formatDate(invoice.dueDate)}
                {invoice.daysOverdue > 0 && (
                  <span className="text-aging-over ml-2 text-xs">+{invoice.daysOverdue}d</span>
                )}
              </td>
              <td className="numeric px-4 py-2.5 text-right">{formatMoney(invoice.total)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
