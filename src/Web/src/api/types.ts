/** Display settings from `GET /api/config`; the server is the single source for money and dates. */
export interface AppConfig {
  currency: string;
  locale: string;
  taxLabel: string;
  defaultVatRate: number;
  accountantMarkPaidLimit: number;
}

export type Role = 'Viewer' | 'Accountant' | 'Admin';

export type InvoiceStatus = 'Draft' | 'Sent' | 'Paid' | 'Cancelled';

export interface AuthenticatedUser {
  email: string;
  displayName: string;
  role: Role;
}

export interface LoginResponse {
  accessToken: string;
  expiresAt: string;
  user: AuthenticatedUser;
}

export interface InvoiceSummary {
  number: string;
  customerName: string;
  status: InvoiceStatus;
  isOverdue: boolean;
  daysOverdue: number;
  issueDate: string;
  dueDate: string;
  total: number;
}

export interface InvoiceLine {
  description: string;
  quantity: number;
  unitPrice: number;
  amount: number;
}

export interface InvoiceDetail extends InvoiceSummary {
  customerId: string;
  customerTaxId: string;
  lines: InvoiceLine[];
  subtotal: number;
  vatRate: number;
  vatAmount: number;
  paidAt: string | null;
}

export interface InvoiceList {
  asOf: string;
  currency: string;
  count: number;
  invoices: InvoiceSummary[];
}

export type AgingBucketKey = 'current' | '1-30' | '31-60' | '60+';

export interface AgingBucket {
  key: AgingBucketKey;
  label: string;
  invoiceCount: number;
  amount: number;
}

export interface ReceivablesReport {
  asOf: string;
  currency: string;
  totalOutstanding: number;
  totalOverdue: number;
  invoiceCount: number;
  buckets: AgingBucket[];
}

export interface InvoiceFilters {
  status?: InvoiceStatus | '';
  customerName?: string;
  overdue?: boolean;
}

/** The events `POST /api/chat` streams over SSE. Mirrors `ChatEvent` on the server. */
export type ChatEvent =
  | { type: 'conversation'; conversationId: string; traceId: string }
  | { type: 'activity'; tool: string; phase: 'start' | 'end'; label: string }
  | { type: 'token'; text: string }
  | {
      type: 'approval_required';
      actionId: string;
      tool: string;
      summary: string;
      expiresAt: string;
    }
  | { type: 'blocked'; tool: string; reason: string }
  | { type: 'done'; conversationId: string; traceId: string }
  | { type: 'error'; message: string };

/** Totals from `GET /api/usage/summary`. Budget figures are the global wallet, not the caller's. */
export interface UsageSummary {
  from: string | null;
  to: string | null;
  conversations: number;
  modelCalls: number;
  promptTokens: number;
  completionTokens: number;
  costEur: number;
  spentTodayEur: number;
  dailyBudgetEur: number;
  budgetExhausted: boolean;
}

export interface ConversationUsageRow {
  conversationId: string;
  startedAt: string;
  userEmail: string;
  models: string;
  modelCalls: number;
  promptTokens: number;
  completionTokens: number;
  toolCalls: number;
  costEur: number;
}

export interface ModelCallRow {
  at: string;
  model: string;
  promptTokens: number;
  completionTokens: number;
  toolCalls: number;
  latencyMs: number;
  costEur: number;
}

export type GateDecision = 'auto' | 'confirmed' | 'denied' | 'blocked';

export interface ToolEventRow {
  at: string;
  tool: string;
  decision: GateDecision;
}

export interface ConversationUsageDetail {
  conversationId: string;
  startedAt: string;
  userEmail: string;
  systemPromptHash: string;
  modelCalls: number;
  promptTokens: number;
  completionTokens: number;
  toolCalls: number;
  costEur: number;
  calls: ModelCallRow[];
  toolEvents: ToolEventRow[];
}

/**
 * What became of a proposed write. `summary` and `message` are both written by the server: the
 * sentence a person agreed to, and the sentence describing what happened, are never the model's.
 */
export interface ActionOutcome {
  actionId: string;
  status: 'approved' | 'rejected' | 'failed';
  summary: string;
  message: string;
}
