import type { InvoiceDeliveryView, InvoiceDetail } from '../api/types';
import { DeliveryPill, StatusPill } from '../components/Pills';
import { appConfig, formatDate, formatDateTime, formatMoney } from '../format';

export function InvoiceDetailPanel({
  invoice,
  onClose,
}: {
  invoice: InvoiceDetail;
  onClose: () => void;
}) {
  return (
    <section aria-labelledby="invoice-detail" className="card slide-in overflow-hidden">
      <header className="border-rule bg-sunken flex flex-wrap items-start gap-3 border-b px-5 py-4">
        <div className="flex-1">
          <h2 id="invoice-detail" className="numeric text-lg">
            {invoice.number}
          </h2>
          <p className="text-ink-soft mt-0.5 text-sm">
            {invoice.customerName} · <span className="numeric">{invoice.customerTaxId}</span>
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <StatusPill status={invoice.status} isOverdue={invoice.isOverdue} />
          {invoice.delivery && <DeliveryPill status={invoice.delivery.status} />}
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close invoice detail"
          className="text-ink-faint hover:text-ink -mt-1 -mr-1 px-1 text-lg leading-none"
        >
          ×
        </button>
      </header>

      {/* A fact about somebody else's system, stated separately from the ledger above it. Sent and
          Failed together is not a contradiction to soften — it is what actually happened. */}
      {invoice.delivery && (
        <p className="border-rule bg-sunken text-ink-soft border-b px-5 py-2 text-xs leading-relaxed">
          {deliverySummary(invoice.delivery)}
        </p>
      )}

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

/**
 * What the delivery status means, in a sentence rather than a word. Keyed on status only, never on
 * the provider's own error text: that text was never meant for a reader on this side of the API and
 * can say more than it should — see `ActionExecutionView.errorDetail`, which stopped shipping the
 * same thing for the same reason.
 */
function deliverySummary(delivery: InvoiceDeliveryView): string {
  const attempts = delivery.attempts > 1 ? ` after ${delivery.attempts} attempts` : '';

  switch (delivery.status) {
    case 'queued':
      return 'Waiting to be sent.';
    case 'delivered':
      return delivery.settledAt
        ? `Delivered ${formatDateTime(delivery.settledAt)}${attempts}.`
        : `Delivered${attempts}.`;
    case 'failed':
      // Authoritative, not a hiccup: the provider refused it, and resending would be refused
      // identically. The invoice stays Sent — the ledger change is real — so the sentence says
      // both things rather than letting "failed" read as "nothing happened".
      return `The provider would not deliver this invoice${attempts}. The invoice was issued, but the customer has not received it.`;
    case 'unknown':
      return `Waiting to confirm with the provider whether this reached the customer${attempts}.`;
    default:
      return delivery.status;
  }
}
