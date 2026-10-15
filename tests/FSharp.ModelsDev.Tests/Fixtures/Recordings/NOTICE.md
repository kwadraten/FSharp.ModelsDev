# HTTP/SSE recordings

The recorder design and the `opencode/` fixtures are adapted from
[anomalyco/opencode](https://github.com/anomalyco/opencode/tree/81200db2a3a86fc72a60dff996557f59865a6f24),
commit `81200db2a3a86fc72a60dff996557f59865a6f24` (MIT, Copyright (c) 2025 opencode).
The upstream license is preserved in [opencode/LICENSE.opencode.txt](opencode/LICENSE.opencode.txt).
The F# handler is a .NET implementation of this approach, without a Bun, Effect,
OpenCode CLI, or JavaScript runtime dependency.

Each imported cassette records its original path and commit in `metadata.source`.
Response bodies are unchanged upstream fixture data; SHA-256 checks enforce that
invariant offline. Importing fixtures does not establish that a live endpoint was
successfully called by this library. In particular, the Gemini cache fixture is
used to check the upstream protocol sample and usage fields, not to demonstrate
a new cache hit against Google.

Requests are adapted to this SDK's supported contract and serialization:

- Chat: omit the upstream forced `tool_choice` option, which `ChatRequest` does not expose.
- Responses: map system instructions to a developer message, use text message
  content strings, omit reasoning-summary/strict-tool options not exposed here,
  and replay full retained function-call state.
- Messages: encode the system instruction as a string.
- Gemini: retain the upstream request shape and repeated identical requests.

Original request envelopes remain in each interaction's `sourceRequest`; they
are not used as a permissive matcher. The adapted `request` must match the real
SDK request in full after header/credential normalization. JSON property order
is ignored, while array order and all JSON fields remain significant.

The two-interaction Responses and Messages fixtures exercise tool calls,
arguments, result replay, MEAI message conversion, terminal events, final text,
and usage. They are not timing, network reliability, or OAuth entitlement tests.

The handler supports the HTTP portion of OpenCode's version-1 cassette envelope:
`version`, `metadata`, and ordered `interactions` with request/response headers
and UTF-8 bodies. Unsupported versions and non-HTTP interactions fail explicitly.
Binary bodies and WebSocket recordings are outside this library's current
HTTP/SSE transport. Replay fragments UTF-8 bytes to exercise SSE readers, but
does not reproduce recorded packet boundaries or timing. Record mode buffers
the upstream response before handing it to the SDK; existing transport tests
continue to cover time-sensitive cancellation and stalled streams.

New local captures go under `local/` and contain one complete call followed by
one streaming call. Once present, the local replay test exercises every such
file offline. Required imported recordings never skip or access the network
when a file is missing. See [AGENTS.md](../../../../AGENTS.md) for commands and
recording constraints.
