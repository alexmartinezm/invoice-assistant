import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { AssistantTool } from '../api/types';
import { RoleBadge } from '../components/Pills';

/**
 * The catalog changes with a deployment, never with a conversation, so it is read once per page
 * rather than every time the panel is opened.
 */
let cached: AssistantTool[] | null = null;

/**
 * What the assistant can do, read from `GET /api/assistant/tools`.
 *
 * The policy file decides what happens to a call; the catalog decides which calls can exist at all,
 * and that is the older and stronger of the two guarantees. It was also the one with nothing to
 * look at — the argument was in the docs and the boundary was in the code, and neither is on screen
 * while somebody is using the thing. A Viewer reading `cancel_invoice · Admin` here learns where
 * the edge is without having to walk into it.
 */
export function ToolCatalogPanel({ token }: { token: string }) {
  const [tools, setTools] = useState<AssistantTool[] | null>(cached);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (cached) return;

    let live = true;

    void api
      .assistantTools(token)
      .then((loaded) => {
        cached = loaded;
        if (live) setTools(loaded);
      })
      .catch(() => {
        if (live) setFailed(true);
      });

    return () => {
      live = false;
    };
  }, [token]);

  return (
    <section
      aria-label="Tool catalog"
      aria-busy={tools === null && !failed}
      className="slide-in border-rule bg-sunken max-h-72 overflow-y-auto border-t px-4 py-3"
    >
      <h3 className="text-ink-faint font-mono text-[11px] tracking-wider uppercase">
        Tools{tools ? ` · ${tools.length}` : ''}
      </h3>

      <p className="text-ink-soft mt-2 text-xs leading-relaxed">
        The whole surface. There is no delete tool, no bulk operation and no write that touches more
        than the one invoice it names.
      </p>

      {failed ? (
        <p className="text-ink-faint mt-3 text-xs">The catalog could not be read.</p>
      ) : (
        // Dimmed while it loads rather than replaced by a spinner: the layout is already correct,
        // and it only has to hold for one request against our own origin.
        <div className={tools === null ? 'opacity-50' : undefined}>
          <ToolGroup heading="Read" tools={tools?.filter((tool) => tool.sideEffect === 'read')} />
          <ToolGroup heading="Write" tools={tools?.filter((tool) => tool.sideEffect === 'write')} />
        </div>
      )}
    </section>
  );
}

function ToolGroup({ heading, tools }: { heading: string; tools: AssistantTool[] | undefined }) {
  if (!tools || tools.length === 0) return null;

  return (
    <>
      <h4 className="text-ink-faint mt-4 font-mono text-[11px] tracking-wider uppercase">
        {heading} · {tools.length}
      </h4>

      <ul className="mt-1">
        {tools.map((tool) => (
          <li key={tool.name} className="border-rule border-t py-2 last:pb-0">
            {/* Wrapping rather than truncating: a tool name is an identifier and a public contract,
                so at 320px the badges drop to their own line instead of eliding it. */}
            <div className="flex flex-wrap items-baseline justify-between gap-x-2 gap-y-1">
              <span className="font-mono text-xs">{tool.name}</span>

              <span className="flex shrink-0 items-center gap-1.5">
                <RoleBadge role={tool.requiredRole} />
                <span
                  title={`Risk level: ${tool.riskLevel}`}
                  className="text-ink-faint font-mono text-[11px]"
                >
                  {tool.riskLevel}
                </span>
              </span>
            </div>

            <p className="text-ink-soft mt-1 text-xs leading-snug" title={tool.description}>
              {firstSentence(tool.description)}
            </p>
          </li>
        ))}
      </ul>
    </>
  );
}

/**
 * Tool descriptions are written for the model: several sentences about when to call it and what not
 * to do with the result. The opening sentence is the part a person needs, and the rest stays on the
 * row's title, so this shortens without hiding.
 */
function firstSentence(description: string): string {
  const end = description.indexOf('. ');
  return end === -1 ? description : description.slice(0, end + 1);
}
