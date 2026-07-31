# Deployment

The demo is one image. The SPA is built and dropped into the API's `wwwroot`, and the same process
that answers `/api` serves the static files, so the browser only ever talks to one origin: there is
no CORS configuration in the repo and nothing to get wrong on a deploy. Beside it runs PostgreSQL.
Migrations and the seeded ledger are applied on startup, so there is no database step.

That leaves two things to arrange anywhere you deploy this: a PostgreSQL to point at, and the
handful of environment variables below.

| File | For |
|---|---|
| `docker-compose.yml` | Running it yourself: locally, or on a VPS you manage |
| `docker-compose.coolify.yml` | Deploying through Coolify, which owns the domain, the TLS and the proxy |

Both build the same `Dockerfile`. The second one publishes no host ports and hardcodes no URL.

## Locally

```bash
cp .env.example .env    # optional: add an AI provider key
docker compose up       # http://localhost:8080
```

The three demo users share the password `demo1234`. Without an AI key the ledger, the filters and
the whole API work; only `/api/chat` answers 503 naming the variables it wants.

PostgreSQL is published on `127.0.0.1:5432` — loopback rather than every interface, because the
compose defaults are `postgres`/`postgres` and this file is also the one people run on a VPS.
`dotnet run` and `dotnet test` on the host still reach it. Set `POSTGRES_BIND=0.0.0.0` if you
genuinely need it from another machine.

## On Coolify

Coolify terminates TLS, issues the certificate and reverse-proxies to the container, so the compose
file it deploys should not publish ports or know its own domain. That is what
`docker-compose.coolify.yml` is: the same two services, minus everything Coolify does better.

1. **New resource → Docker Compose**, pointed at this repository and the branch you want.
2. Set **Docker Compose Location** to `/docker-compose.coolify.yml`. The build context stays the
   repository root, because the Dockerfile copies from `src/Web` and `src/Api` both.
3. Under **Environment Variables**, set the two that have no default:

   | Variable | Value |
   |---|---|
   | `JWT_SIGNING_KEY` | 32+ random characters — anyone who knows it can mint an Admin token |
   | `POSTGRES_PASSWORD` | the database password; name it `SERVICE_PASSWORD_POSTGRES` instead and Coolify generates and stores one for you |

   The deploy fails loudly if either is missing, which is the intended behaviour: a public demo
   should not come up on credentials that are printed in a public repository.

4. Add the AI provider variables if you want the assistant to answer — `AI_PROVIDER` plus either
   the three `AZURE_OPENAI_*` or `OPENAI_API_KEY` and `CHAT_MODEL`. Skip them and everything except
   `/api/chat` still works, which is a reasonable way to publish the ledger first and add the key
   once the deploy is green.

   `AI_PROVIDER` defaults to `azure-openai`, so setting `OPENAI_API_KEY` and `CHAT_MODEL` is not
   enough on its own: set `AI_PROVIDER=openai-compatible` as well, or the Azure branch is chosen and
   the chat reports the Azure variables missing. Leave `OPENAI_BASE_URL` empty for OpenAI itself;
   it is for pointing at a compatible endpoint. Whichever model you name in `CHAT_MODEL`, check it
   has a price under `Usage:Prices` in `appsettings.json` — matching is by longest model-id prefix,
   and a model with no entry is metered at zero, which makes its spend invisible to the daily cap.
5. On the **app** service, set the domain, written with the container port:
   `https://invoices.example.com:8080`. The `:8080` is how Coolify knows which port behind the
   proxy the domain belongs to; it does not appear in the public URL. Leave the `postgres` service
   without a domain — it is reachable from the app over the stack's internal network and from
   nowhere else.
6. Deploy. The first boot runs the migrations and seeds around 40 invoices; the container's health
   check turns green once `/health` answers.

Point `USAGE_DAILY_BUDGET_EUR` at a number you are willing to lose before you hand the URL out. It
is a global daily cap in euros, checked before the turn starts and before every model call inside
it, and it is the thing that makes a public demo with a real key in it safe to leave running — a
per-user rate limit does nothing about a scraper with many IPs. See
[ADR 008](adr/008-cost-accounting-and-the-spend-kill-switch.md).

### Using a database Coolify manages

If you would rather run PostgreSQL as its own Coolify resource than as part of the stack, delete the
`postgres` service and the `depends_on` block from the compose file and set
`POSTGRES_CONNECTION_STRING` to what the panel gives you. Both shapes are accepted: an Npgsql
keyword string, or the `postgres://user:pass@host:port/db` URL that panels tend to hand out.

## On any other host

Anything that can run a container works, because the image takes its whole configuration from
environment variables. Build and run it directly:

```bash
docker build -t invoice-assistant .
docker run -p 8080:8080 \
  -e POSTGRES_CONNECTION_STRING='Host=...;Port=5432;Database=...;Username=...;Password=...' \
  -e JWT_SIGNING_KEY='32+ random characters' \
  invoice-assistant
```

Behind any TLS-terminating proxy — Traefik, Caddy, nginx, a cloud load balancer — set the two
variables the Coolify file sets for you: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so the app sees
the real scheme and client IP rather than the proxy's, and `ASSISTANT_API_BASE_URL=http://localhost:8080`
for the reason in the next section.

## The one deployment-specific setting worth understanding

The assistant's tools do not call the domain layer directly. They go over HTTP to our own REST API
carrying the caller's bearer token, so endpoint authorization applies to the assistant exactly as it
applies to the browser ([ADR 002](adr/002-tools-via-http.md)). That means the app makes HTTP
requests to itself, and it has to know where "itself" is.

Left unset, it derives the base URL from the incoming request. That is right in development and
right on a naked port. Behind a proxy that terminates TLS it is wrong in a way that is annoying to
diagnose: the request arrives as plain HTTP, so the app builds `http://your-public-domain`, the
call leaves the container, the proxy answers with a redirect to HTTPS, and .NET drops the
`Authorization` header on a redirect that changes origin. Every tool call then comes back
`unauthenticated` while the rest of the app looks perfectly healthy.

`ASSISTANT_API_BASE_URL=http://localhost:8080` keeps that call inside the container, where it does
not depend on how the outside world reaches the app. Both compose files set it.

## Seeing the traces

The chat footer prints the trace id of each turn, which is only useful if the id leads somewhere.
Nothing is exported by default in production, so there are two ways to make it lead somewhere.

The cheap one, for a local run: `OTEL_CONSOLE_EXPORTER=true` writes the spans to stdout. It is the
default in Development, so `dotnet run` already has it.

The real one: point `OTEL_EXPORTER_OTLP_ENDPOINT` at any OTLP collector. It takes both compose files
without editing either, and no collector ships in them — a trace viewer is infrastructure you
probably already have, and one more service in the demo's compose file is one more thing to explain.
Anything speaking OTLP works; a throwaway one is a container away:

```bash
docker run -d --name aspire-dashboard -p 18888:18888 -p 4317:18889 \
  mcr.microsoft.com/dotnet/aspire-dashboard:9.0
# then, on the app: OTEL_EXPORTER_OTLP_ENDPOINT=http://host.docker.internal:4317
```

What you get, per turn: `assistant.turn`, one `assistant.tool_call` per tool tagged with the gate's
decision (`allowed`, `pending_approval`, `denied` or `blocked`), and one `assistant.model_call` per
call to the model tagged with model, tokens, latency and cost. Metrics arrive on the same endpoint
under the `InvoiceAssistant.Assistant` meter — model calls, tokens by direction, spend, budget
rejections and unpriced calls — which is the only place the kill switch's counters are visible.

One thing to know before you open the viewer: `assistant.turn` is a **sibling** of the tool and model
spans rather than their parent, so a turn does not collapse into one subtree. Everything shares the
request's trace id — the one the chat footer shows — so nothing is lost and correlation works. The
reason, and what fixing it would take, is in [`architecture.md`](architecture.md#cost-traces-and-the-kill-switch).

## Checking a deploy

```bash
curl https://invoices.example.com/health                  # {"status":"ok"}
curl https://invoices.example.com/api/auth/demo-users     # the three seeded users
```

Then log in as `ana@demo` / `demo1234` and ask the assistant something that needs a tool, like
"which invoices are overdue?" — that exercises the self-call above, which is the part a proxy can
break without anything else looking wrong.

| Symptom | Cause |
|---|---|
| Every tool call answers `unauthenticated`, the ledger works | `ASSISTANT_API_BASE_URL` not set behind a TLS-terminating proxy |
| `/api/chat` answers 503 | No AI provider configured; the message names the variables it wants |
| `/api/chat` answers 429 | Either the per-user turn limit or the daily spend cap; the Usage page tells you which |
| The container never turns healthy | Almost always the database: check `POSTGRES_CONNECTION_STRING` and that the password matches the one PostgreSQL was created with |
| The chat replies arrive all at once at the end | Something between the browser and the app is buffering the SSE stream. Traefik and Caddy do not; Cloudflare's proxy and nginx without `proxy_buffering off` do |
