import type { ConversationUsageRow } from '../api/types';
import { formatCostEur, formatCount, formatDateTime } from '../format';

export function ConversationsTable({
  rows,
  selectedId,
  onSelect,
}: {
  rows: ConversationUsageRow[];
  selectedId: string | null;
  onSelect: (conversationId: string) => void;
}) {
  if (rows.length === 0) {
    return (
      <p className="text-ink-soft border-rule rounded-lg border border-dashed px-6 py-12 text-center text-sm">
        No conversations metered yet — ask the assistant something and its cost will appear here.
      </p>
    );
  }

  return (
    <div className="card overflow-x-auto">
      <table className="w-full min-w-2xl border-collapse text-sm">
        <caption className="sr-only">Metered assistant conversations</caption>
        <thead>
          <tr className="border-rule bg-sunken text-ink-faint border-b text-left font-mono text-[11px] tracking-wider uppercase">
            <th scope="col" className="px-4 py-2.5 font-medium">
              Started
            </th>
            <th scope="col" className="px-4 py-2.5 font-medium">
              User
            </th>
            <th scope="col" className="px-4 py-2.5 font-medium">
              Model
            </th>
            <th scope="col" className="px-4 py-2.5 text-right font-medium">
              Calls
            </th>
            <th scope="col" className="px-4 py-2.5 text-right font-medium">
              Tools
            </th>
            <th scope="col" className="px-4 py-2.5 text-right font-medium">
              Tokens in / out
            </th>
            <th scope="col" className="px-4 py-2.5 text-right font-medium">
              Cost
            </th>
          </tr>
        </thead>

        <tbody>
          {rows.map((row, index) => (
            <tr
              key={row.conversationId}
              onClick={() => onSelect(row.conversationId)}
              className={`ledger-row border-rule hover:bg-sunken cursor-pointer border-b transition-colors last:border-b-0 ${
                selectedId === row.conversationId ? 'bg-accent-soft' : ''
              }`}
              style={{ animationDelay: `${Math.min(index, 20) * 18}ms` }}
            >
              <td className="px-4 py-2.5">
                <button
                  type="button"
                  className="numeric text-accent-ink text-left whitespace-nowrap"
                  aria-current={selectedId === row.conversationId}
                >
                  {formatDateTime(row.startedAt)}
                </button>
              </td>
              <td className="max-w-48 truncate px-4 py-2.5 font-mono text-xs">{row.userEmail}</td>
              <td className="max-w-48 truncate px-4 py-2.5 font-mono text-xs">{row.models}</td>
              <td className="numeric text-ink-soft px-4 py-2.5 text-right">{row.modelCalls}</td>
              <td className="numeric text-ink-soft px-4 py-2.5 text-right">{row.toolCalls}</td>
              <td className="numeric text-ink-soft px-4 py-2.5 text-right whitespace-nowrap">
                {formatCount(row.promptTokens)} / {formatCount(row.completionTokens)}
              </td>
              <td className="numeric px-4 py-2.5 text-right">{formatCostEur(row.costEur)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
