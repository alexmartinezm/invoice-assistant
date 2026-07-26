/**
 * A tool call the gate refused — denied by policy, or over a per-turn limit.
 *
 * Bordered and text-coloured in `danger`, never filled: filled is reserved for settled money, and
 * `danger` is deliberately not the aging ramp so that red in this app keeps meaning "overdue" and
 * nothing else.
 *
 * This is not an error state. Nothing went wrong — the system did exactly its job — so it reads as
 * a statement rather than an alarm, and the reason comes from the server so it says which rule
 * stopped it rather than "something went wrong".
 */
export function BlockCard({ tool, reason }: { tool: string; reason: string }) {
  return (
    <section
      aria-label="Blocked"
      className="slide-in border-danger text-danger rounded-md border px-3 py-2"
    >
      <p className="flex items-baseline gap-2 text-xs font-semibold tracking-wide uppercase">
        Blocked
        <span className="text-ink-faint font-mono text-[11px] normal-case">{tool}</span>
      </p>

      <p className="text-ink mt-1 text-sm leading-relaxed break-words">{reason}</p>
    </section>
  );
}
