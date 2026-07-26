# Verifiable commands

Single source of commands for humans and agents. Update this file in the same commit that adds or changes projects, scripts or tooling.

## Current state

The code does not exist yet (see Roadmap in `README.md`). Commands will be filled in per phase; the ones marked _pending_ must not be invented or assumed.

## Setup

```bash
cp .env.example .env   # add the provider API key
```

- Backend restore: _pending (F1)_ — planned `dotnet restore`
- Frontend install: _pending (F1)_ — planned `npm install` in `src/Web`

## Development

- API: _pending (F1)_ — planned `dotnet run --project src/Api`
- Web: _pending (F1)_ — planned `npm run dev` in `src/Web`

## Tests

- Backend suite: _pending (F1)_ — planned `dotnet test`
- Evals (requires API key): _pending (F3)_ — planned `dotnet test evals/InvoiceAssistant.Evals`

## Quality gates

- Formatting: _pending (F1)_ — planned `dotnet format` + Prettier/ESLint in `src/Web`
- Full build: _pending (F1)_
- `policies.json` validation: `jq empty policies.json`
