import { useEffect, useState } from 'react';
import { api, ApiError } from '../api/client';
import type { ConversationUsageDetail, ConversationUsageRow, UsageSummary } from '../api/types';
import { useSession } from '../auth/useAuth';
import { ConversationDetailPanel } from '../usage/ConversationDetailPanel';
import { ConversationsTable } from '../usage/ConversationsTable';
import { UsageStrip } from '../usage/UsageStrip';

/**
 * What the assistant costs: the day's wallet, then every metered conversation. Non-admins see
 * their own; an Admin sees everyone's — the scoping is the server's, this page just renders it.
 */
export function UsagePage() {
  const { session } = useSession();
  const token = session.accessToken;

  const [summary, setSummary] = useState<UsageSummary | null>(null);
  const [rows, setRows] = useState<ConversationUsageRow[]>([]);
  const [selected, setSelected] = useState<ConversationUsageDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      setLoading(true);

      try {
        const [usage, conversations] = await Promise.all([
          api.usageSummary(token),
          api.usageConversations(token),
        ]);

        if (cancelled) return;
        setSummary(usage);
        setRows(conversations);
        setError(null);
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof ApiError ? cause.message : 'Could not load the usage ledger.');
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void load();
    return () => {
      cancelled = true;
    };
  }, [token]);

  async function openConversation(conversationId: string) {
    if (selected?.conversationId === conversationId) {
      setSelected(null);
      return;
    }

    try {
      setSelected(await api.usageConversation(token, conversationId));
    } catch (cause) {
      setError(cause instanceof ApiError ? cause.message : 'Could not open the conversation.');
    }
  }

  return (
    <div className="mx-auto max-w-7xl space-y-6 px-6 py-8">
      {error && (
        <p role="alert" className="border-danger text-danger rounded-md border px-4 py-3 text-sm">
          {error}
        </p>
      )}

      {summary && <UsageStrip summary={summary} />}

      {/* Above the table for the same reason the invoice detail is: a panel below forty rows is
          a panel nobody sees. */}
      {selected && <ConversationDetailPanel detail={selected} onClose={() => setSelected(null)} />}

      <div aria-busy={loading} className={loading ? 'opacity-60 transition-opacity' : undefined}>
        <ConversationsTable
          rows={rows}
          selectedId={selected?.conversationId ?? null}
          onSelect={(id) => void openConversation(id)}
        />
      </div>
    </div>
  );
}
