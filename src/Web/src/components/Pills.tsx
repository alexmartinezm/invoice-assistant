import type { InvoiceDeliveryView, InvoiceStatus, Role } from '../api/types';

const statusStyles: Record<InvoiceStatus, string> = {
  Draft: 'text-ink-soft border-rule-strong border-dashed',
  Sent: 'text-accent border-accent',
  // Collected money is the only filled badge: it is the state you are looking for.
  Paid: 'text-accent-ink border-accent bg-accent-soft',
  Cancelled: 'text-ink-faint border-rule line-through',
};

export function StatusPill({ status, isOverdue }: { status: InvoiceStatus; isOverdue: boolean }) {
  // Overdue is a derived reading of a Sent invoice, never a status of its own — the badge says
  // so rather than inventing a fifth state.
  if (isOverdue) {
    return (
      <span className="text-aging-over border-aging-over inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium">
        Overdue
      </span>
    );
  }

  return (
    <span
      className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${statusStyles[status]}`}
    >
      {status}
    </span>
  );
}

const deliveryStyles: Record<InvoiceDeliveryView['status'], string> = {
  // Not yet known either way — neutral, the same register a running tool chip uses, never the
  // aging ramp: work in flight is not a warning.
  queued: 'rounded-full text-ink-soft border-rule-strong border-dashed',
  // Confirmed, but not money, so not filled — filled stays reserved for collected money.
  delivered: 'rounded-full text-accent border-accent',
  // A refusal, styled like one: BlockCard and ResolvedNote's own failed treatment is
  // `rounded-md`, not the `rounded-full` every status/role pill uses, precisely because `danger`
  // and `aging-over` render as near-identical brick reds (#a32f2f vs #a3311d) — on an overdue
  // invoice the two pills would sit side by side and read as the same signal if shape didn't
  // separate them too. Colour alone does not carry this distinction; shape has to help.
  failed: 'rounded-md text-danger border-danger',
  unknown: 'rounded-full text-ink-soft border-rule',
};

const deliveryLabels: Record<InvoiceDeliveryView['status'], string> = {
  queued: 'Delivery queued',
  delivered: 'Delivered',
  failed: 'Delivery failed',
  unknown: 'Delivery unconfirmed',
};

/**
 * What became of an invoice's delivery — a fact about somebody else's system, reported next to but
 * never folded into the invoice's own status pill. An invoice reading Sent while this reads Failed
 * is not a contradiction: the ledger moved and the email did not, and hiding either half would be
 * the dishonest choice.
 */
export function DeliveryPill({ status }: { status: InvoiceDeliveryView['status'] }) {
  return (
    <span
      className={`inline-flex items-center gap-1 border px-2 py-0.5 text-xs font-medium ${deliveryStyles[status]}`}
    >
      {status === 'unknown' && (
        <span aria-hidden="true" className="bg-ink-soft h-1.5 w-1.5 rounded-full" />
      )}
      {deliveryLabels[status]}
    </span>
  );
}

const roleStyles: Record<Role, string> = {
  Admin: 'text-accent-ink border-accent bg-accent-soft',
  Accountant: 'text-ink border-rule-strong bg-sunken',
  Viewer: 'text-ink-soft border-rule',
};

export function RoleBadge({ role }: { role: Role }) {
  return (
    <span
      className={`inline-flex items-center rounded border px-1.5 py-0.5 font-mono text-[11px] tracking-wide uppercase ${roleStyles[role]}`}
    >
      {role}
    </span>
  );
}
