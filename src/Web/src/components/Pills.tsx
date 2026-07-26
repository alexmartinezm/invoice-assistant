import type { InvoiceStatus, Role } from '../api/types';

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
