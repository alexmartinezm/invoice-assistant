import type {
  ActionOutcome,
  AppConfig,
  AuthenticatedUser,
  ChatEvent,
  ConversationUsageDetail,
  ConversationUsageRow,
  InvoiceDetail,
  InvoiceFilters,
  InvoiceList,
  LoginResponse,
  PendingActionView,
  ReceivablesReport,
  UsageSummary,
} from './types';

/** An error carrying what the API actually said, so the UI can show the real reason. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;

  constructor(status: number, message: string, code?: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

interface ProblemDetails {
  title?: string;
  detail?: string;
  code?: string;
}

async function request<T>(path: string, token: string | null, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  });

  if (!response.ok) {
    throw await toApiError(response);
  }

  return (await response.json()) as T;
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = {};

  try {
    problem = (await response.json()) as ProblemDetails;
  } catch {
    // Not every failure comes back as ProblemDetails; the status alone will have to do.
  }

  return new ApiError(
    response.status,
    problem.detail ?? problem.title ?? `Request failed with status ${response.status}.`,
    problem.code,
  );
}

export const api = {
  /** Anonymous: the login screen quotes the settlement limit before anyone has a token. */
  config: () => request<AppConfig>('/api/config', null),

  /** Anonymous: the seeded users, so the login selector never keeps its own stale copy. */
  demoUsers: () => request<AuthenticatedUser[]>('/api/auth/demo-users', null),

  login: (email: string, password: string) =>
    request<LoginResponse>('/api/auth/login', null, {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }),

  invoices: (token: string, filters: InvoiceFilters) => {
    const query = new URLSearchParams({ limit: '100' });
    if (filters.status) query.set('status', filters.status);
    if (filters.customerName) query.set('customerName', filters.customerName);
    if (filters.overdue) query.set('overdue', 'true');

    return request<InvoiceList>(`/api/invoices?${query}`, token);
  },

  invoice: (token: string, number: string) =>
    request<InvoiceDetail>(`/api/invoices/${encodeURIComponent(number)}`, token),

  receivables: (token: string) => request<ReceivablesReport>('/api/reports/receivables', token),

  /**
   * Approving runs the write under this user's own token, and the server re-checks policy before
   * it does — the button is a decision, not an instruction the API takes on trust.
   */
  resolveAction: (token: string, actionId: string, decision: 'approve' | 'reject') =>
    request<ActionOutcome>(`/api/actions/${encodeURIComponent(actionId)}/${decision}`, token, {
      method: 'POST',
    }),

  /**
   * Proposals still waiting on this user — their own, plus anything escalated to them. An Admin has
   * always been allowed to resolve somebody else's proposal; without this there was no way to find
   * one before it expired.
   */
  openActions: (token: string) => request<PendingActionView[]>('/api/actions', token),

  usageSummary: (token: string) => request<UsageSummary>('/api/usage/summary', token),

  usageConversations: (token: string) =>
    request<ConversationUsageRow[]>('/api/usage/conversations', token),

  usageConversation: (token: string, conversationId: string) =>
    request<ConversationUsageDetail>(
      `/api/usage/conversations/${encodeURIComponent(conversationId)}`,
      token,
    ),
};

/**
 * Streams a chat turn. `EventSource` cannot POST, so the turn is a fetch whose body is parsed as
 * SSE by hand — which is also why the server's event names and the `type` discriminator match.
 */
export async function streamChatTurn(
  token: string,
  body: { message: string; conversationId: string | null },
  onEvent: (event: ChatEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  const response = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
    signal,
  });

  if (!response.ok || !response.body) {
    throw await toApiError(response);
  }

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;

    buffer += value;

    // Events are separated by a blank line; anything after the last one is still arriving.
    const chunks = buffer.split('\n\n');
    buffer = chunks.pop() ?? '';

    for (const chunk of chunks) {
      const event = parseEvent(chunk);
      if (event) onEvent(event);
    }
  }
}

function parseEvent(chunk: string): ChatEvent | null {
  const data = chunk
    .split('\n')
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice('data:'.length).trim())
    .join('\n');

  if (!data) return null;

  try {
    return JSON.parse(data) as ChatEvent;
  } catch {
    return null;
  }
}
