import type { InvoiceDetail } from '../api/types';
import { StatusPill } from '../components/Pills';
import { appConfig, formatDate, formatMoney } from '../format';

export function InvoiceDetailPanel({
  invoice,
  onClose,
}: {
  invoice: InvoiceDetail;
  onClose: () => void;
}) {
  return (
    <section aria-labelledby="invoice-detail" className="card slide-in overflow-hidden">
      <header className="border-rule bg-sunken flex items-start gap-3 border-b px-5 py-4">
        <div className="flex-1">
          <h2 id="invoice-detail" className="numeric text-lg">
            {invoice.number}
          </h2>
          <p className="text-ink-soft mt-0.5 text-sm">
            {invoice.customerName} · <span className="numeric">{invoice.customerTaxId}</span>
          </p>
        </div>
        <StatusPill status={invoice.status} isOverdue={invoice.isOverdue} />
        <button
          type="button"
          onClick={onClose}
          aria-label="Close invoice detail"
          className="text-ink-faint hover:text-ink -mt-1 -mr-1 px-1 text-lg leading-none"
        >
          ×
        </button>
      </header>

      <dl className="border-rule grid grid-cols-2 gap-y-2 border-b px-5 py-4 text-sm sm:grid-cols-4">
        <Field label="Issued" value={formatDate(invoice.issueDate)} />
        <Field label="Due" value={formatDate(invoice.dueDate)} />
        <Field
          label="Days overdue"
          value={invoice.daysOverdue > 0 ? `${invoice.daysOverdue}` : '—'}
          emphasis={invoice.daysOverdue > 0}
        />
        <Field label="Paid" value={invoice.paidAt ? formatDate(invoice.paidAt) : '—'} />
      </dl>

      <table className="w-full border-collapse text-sm">
        <thead>
          <tr className="text-ink-faint border-rule border-b text-left font-mono text-[11px] tracking-wider uppercase">
            <th scope="col" className="px-5 py-2 font-medium">
              Description
            </th>
            <th scope="col" className="px-2 py-2 text-right font-medium">
              Qty
            </th>
            <th scope="col" className="px-2 py-2 text-right font-medium">
              Unit
            </th>
            <th scope="col" className="px-5 py-2 text-right font-medium">
              Amount
            </th>
          </tr>
        </thead>
        <tbody>
          {invoice.lines.map((line, index) => (
            <tr key={`${line.description}-${index}`} className="border-rule border-b">
              {/* Line text is customer data. It is rendered as text, never as markup: one of the
                  seeded invoices carries a prompt injection in its description on purpose. */}
              <td className="px-5 py-2.5 break-words">{line.description}</td>
              <td className="numeric text-ink-soft px-2 py-2.5 text-right">{line.quantity}</td>
              <td className="numeric text-ink-soft px-2 py-2.5 text-right">
                {formatMoney(line.unitPrice)}
              </td>
              <td className="numeric px-5 py-2.5 text-right">{formatMoney(line.amount)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <dl className="ml-auto grid max-w-xs grid-cols-2 gap-y-1 px-5 py-4 text-sm">
        <dt className="text-ink-soft">Subtotal</dt>
        <dd className="numeric text-right">{formatMoney(invoice.subtotal)}</dd>

        <dt className="text-ink-soft">
          {appConfig().taxLabel} ({(invoice.vatRate * 100).toFixed(0)}%)
        </dt>
        <dd className="numeric text-right">{formatMoney(invoice.vatAmount)}</dd>

        <dt className="border-rule mt-1 border-t pt-1 font-semibold">Total</dt>
        <dd className="numeric border-rule mt-1 border-t pt-1 text-right font-semibold">
          {formatMoney(invoice.total)}
        </dd>
      </dl>
    </section>
  );
}

function Field({
  label,
  value,
  emphasis = false,
}: {
  label: string;
  value: string;
  emphasis?: boolean;
}) {
  return (
    <div>
      <dt className="text-ink-faint font-mono text-[11px] tracking-wider uppercase">{label}</dt>
      <dd className={`numeric mt-0.5 ${emphasis ? 'text-aging-over' : ''}`}>{value}</dd>
    </div>
  );
}
