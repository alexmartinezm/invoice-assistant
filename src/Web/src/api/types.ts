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
  /** The invoice's ETag. An approved assistant write only lands if this has not moved. */
  revision: number;
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
  /** How many invoices this response carries. */
  count: number;
  /** How many match the filters in total; larger than `count` when the page was cut short. */
  total: number;
  truncated: boolean;
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

/**
 * One entry of `GET /api/assistant/tools` — the catalog the model is handed, with the metadata the
 * policy engine decides on. This is the capability boundary: whatever is not in it cannot be asked
 * for, whatever the prompt or the user says.
 */
export interface AssistantTool {
  name: string;
  description: string;
  sideEffect: 'read' | 'write';
  requiredRole: Role;
  riskLevel: 'low' | 'medium' | 'high';
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
      canApprove: boolean;
      requiredRole: string;
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

/** Where an attempt at an approved write has got to. `unknown` is not a synonym for `failed`. */
export type ExecutionStatus = 'pending' | 'executing' | 'succeeded' | 'failed' | 'unknown';

/**
 * What became of a proposed write. `summary` and `message` are both written by the server: the
 * sentence a person agreed to, and the sentence describing what happened, are never the model's.
 *
 * The decision and the execution are separate fields because they answer different questions.
 * `decisionStatus` is about a person — did somebody approve this? `executionStatus` is about the
 * world — did it happen? Collapsing them would mean rendering "approved" as "done", which is the
 * claim the server is no longer willing to make on an unconfirmed write.
 */
export interface ActionOutcome {
  actionId: string;
  executionId: string | null;
  decisionStatus: 'approved' | 'rejected' | 'expired' | 'pending';
  executionStatus: ExecutionStatus | null;
  summary: string;
  message: string;
  deliveryStatus: string | null;
}

/** The attempt a decision authorized, as the API reports it. */
export interface ActionExecutionView {
  executionId: string;
  actionId: string | null;
  tool: string;
  decision: 'auto' | 'confirmed';
  status: ExecutionStatus;
  attempts: number;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  errorCode: string | null;
  errorDetail: string | null;
  deliveryId: string | null;
  deliveryStatus: string | null;
}

/** A proposal waiting on somebody, as the server describes it to whoever is looking. */
export interface PendingActionView {
  actionId: string;
  tool: string;
  summary: string;
  status: string;
  createdAt: string;
  expiresAt: string;
  /** True when this user is the one who proposed it. */
  mine: boolean;
  /**
   * Whether this user clears the tool's role floor. Not a promise that approving succeeds — policy
   * is re-checked server-side — but when false, approving cannot work, so no button is offered.
   */
  canApprove: boolean;
  requiredRole: string | null;
  /** The attempt this decision authorized, once there is one. */
  execution: ActionExecutionView | null;
}
