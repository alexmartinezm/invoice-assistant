# ADR 002 · Tools call our own API over HTTP

**Status:** accepted · 2026-07

## Context

The assistant's tools could invoke domain services in-process (more efficient) or call our own REST API over HTTP.

## Decision

Tools call the **own REST API over HTTP with the logged-in user's bearer token**.

## Consequences

- The assistant is "just another client" of the API: endpoint authZ always applies (defense in depth) and the assistant can never do more than the user.
- Identity propagation is visible in OpenTelemetry traces, which is exactly what this repo wants to teach.
- Cost: one extra HTTP call per tool call. In a real client engagement in-process would be evaluated; here didactic visibility wins.
