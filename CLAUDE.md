# bill-assistant

RAG over household utility bill PDFs. Upload a bill, it gets parsed, chunked, embedded and stored in
Qdrant; then you ask questions in natural language ("what did I pay for electricity in July?", "is my
water usage trending up?") and get answers grounded in the actual PDFs, with citations.

- `/api` — .NET 10 Web API (ingestion, retrieval, chat)
- `/web` — Angular app, standalone components, TypeScript
- `/tools/SampleBillGenerator` — writes the synthetic bills in `/samples`
- `/samples` — generated bill PDFs used by the demo and the integration tests

## Environment

| Thing | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.401 | Pinned in `global.json` (`rollForward: latestPatch`) |
| Node | 24.x | |
| Angular CLI | 21.x | |
| Qdrant | 1.19 | `docker compose up -d`. REST on 6333, **gRPC on 6334** |
| Ollama | 0.33+ | `localhost:11434`, models `llama3.2` + `nomic-embed-text` |

Both backing services must be running before the API will do anything useful. `GET /health` performs
a real embedding round-trip and a real vector-store call, and names whichever one is down.

## Architecture

Two pipelines. Keep them separate — they share the embedding generator and the vector collection,
nothing else.

```
INGEST   PDF ──PdfPig──> page text ──chunker──> chunks ──IEmbeddingGenerator──> vectors
                 │                                                                 │
                 └──IChatClient (structured extraction)──> BillMetadata ──> SQLite  │
                                                                                    v
                                                     Qdrant "bill_chunks_nomic_embed_text"

QUERY    question ──IEmbeddingGenerator──> vector ──VectorStoreCollection.SearchAsync──> top-K chunks
             │                                                                              │
             └──(numeric question?)──> SQL totals ─────────┐                                │
                                                           v                                v
                                        answer + citations <── IChatClient (grounded prompt)
```

### Layering

`Core` holds the domain and the interfaces and references only *abstraction* packages
(`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.VectorData.Abstractions`).
`Infrastructure` holds every adapter — Ollama/Azure, Qdrant, EF Core, PdfPig. `Api` is minimal-API
endpoints and DTOs.

That layering is the enforcement mechanism: **a provider SDK type cannot appear in domain code,
because Core does not reference the package.** It is a compile error, not a convention.

| Project | Contains | May reference |
|---|---|---|
| `BillAssistant.Core` | models, interfaces, `BillChunker`, `QuestionAnalyzer`, `BillMetadataValidator`, prompts | abstractions only |
| `BillAssistant.Infrastructure` | `Ai/`, `Pdf/`, `Persistence/`, `Vectors/`, `Ingestion/`, `Retrieval/` | anything |
| `BillAssistant.Api` | `Endpoints/`, `Contracts/`, `Program.cs` | Core + Infrastructure |
| `BillAssistant.Core.Tests` | unit + contract + (skipped) integration tests | all of the above |

### Provider abstraction (the core design constraint)

All model access goes through `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>`. The
concrete provider is chosen from configuration at startup:

```jsonc
"Ai": {
  "Provider": "Ollama",              // "Ollama" | "AzureOpenAI"
  "Ollama":      { "Endpoint": "http://localhost:11434", "ChatModel": "llama3.2",
                   "EmbeddingModel": "nomic-embed-text", "EmbeddingDimensions": 768, "NumCtx": 8192 },
  "AzureOpenAI": { "Endpoint": "", "ApiKey": "", "ChatDeployment": "gpt-4o-mini",
                   "EmbeddingDeployment": "text-embedding-3-small", "EmbeddingDimensions": 1536 }
}
```

Wiring lives in exactly one file: `Infrastructure/Ai/AiServiceCollectionExtensions.cs`. A switch on
`Ai:Provider` registers either OllamaSharp's `OllamaApiClient` (it implements both MEAI interfaces) or
`AzureOpenAIClient` adapted with `.AsIChatClient()` / `.AsIEmbeddingGenerator()`. Swapping to Azure is
a config change plus that existing `case` — no application code changes.

Anything genuinely provider-specific goes in a delegating client, not in a caller. See
`Ai/OllamaDefaultsChatClient.cs`, which sets `num_ctx` through `ChatOptions.RawRepresentationFactory`.

### Vector store

`Microsoft.Extensions.VectorData` abstractions with the **CommunityToolkit.VectorData.Qdrant**
provider. `Vectors/VectorDataBillChunkStore.cs` is named for the abstraction because nothing in it is
Qdrant-specific — the tests run the same class on `CommunityToolkit.VectorData.InMemory`.

The collection model is built at runtime in `Vectors/BillChunkSchema.cs`, not with
`[VectorStoreVector]` attributes, because the embedding dimension is configuration and attribute
arguments must be compile-time constants.

Indexed payload fields (`BillId`, `Utility`, `PeriodStartDay`, `PeriodEndDay`) exist so retrieval can
pre-filter before the vector search — "only electricity bills from 2025" — which matters a lot for
accuracy here.

### Domain model: household bill tracker

During ingestion the chat model also does **structured extraction** into `BillMetadata` (utility,
provider, account, service period, amount due, due date, usage + unit). That metadata is persisted to
SQLite and copied onto every chunk of that bill.

Why it matters: semantic search answers "what does my bill say about late fees" well and answers "how
much did I pay in total last quarter" badly — that is arithmetic over structured fields, not
retrieval. Numeric questions are served from SQL (`EfBillRepository.ComputeTotalsAsync`), passed to the
model in a `<totals>` block, and the system prompt tells it those figures are exact and must not be
contradicted. The model is never asked to add up money.

The same applies to direction. `BillTotals.Series` carries one `PeriodTotal` per matched bill, oldest
first, and `TrendSummary.Describe` turns the first and last into a stated fact ("usage rose 36% from
14 to 19 CCF"). Without it, llama3.2 answered "is my water usage going up?" from whichever number was
nearest in an excerpt, and once claimed a bill was missing that the totals block was listing. Comparing
two numbers is arithmetic, so it happens in code.

`QuestionAnalyzer` (pure, in Core) decides whether a question is numeric - including direction wording
like "going up" and "more expensive" - and infers a utility and date window from its wording.

### API surface

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/bills` | Upload PDF (multipart), ingest, return bill + extracted metadata |
| GET | `/api/bills` | List bills (`utility`, `from`, `to`, `limit`) |
| GET | `/api/bills/{id}` | One bill |
| DELETE | `/api/bills/{id}` | Delete bill and its chunks |
| POST | `/api/chat` | Ask; returns answer + citations + totals |
| POST | `/api/chat/stream` | Same, SSE (`sources` event first, then `token`s, then `done`) |
| GET | `/health` | API + Ollama + Qdrant, with dimension check |

Scalar API docs at `/scalar` in development.

### Web app

Standalone components, signals, no NgModules, zoneless. `core/` holds the typed services
(`BillsService` owns the bill-list signal; `ChatService` parses the SSE stream with `fetch`, because
`HttpClient` would only deliver the body once complete). Features: `bills/` (drag-drop upload, list,
delete) and `chat/` (ask, streamed answer, citation chips). `proxy.conf.json` sends `/api` and
`/health` to `localhost:5280`, so no CORS config is needed in development.

## Commands

```bash
docker compose up -d                        # Qdrant (6333 REST / 6334 gRPC)
ollama pull llama3.2 && ollama pull nomic-embed-text

# api  (run from /api)
dotnet run --project BillAssistant.Api --urls http://localhost:5280
dotnet test                                 # 116 unit/contract tests; integration ones skip
BILLS_INTEGRATION=1 dotnet test --filter Category=Integration   # needs Ollama + Qdrant up
dotnet ef migrations add <Name> --project BillAssistant.Infrastructure \
    --startup-project BillAssistant.Api --output-dir Persistence/Migrations

# web  (run from /web)
npm start                                   # ng serve on :4200, proxies to the API
npx ng test --watch=false

# regenerate the sample bills
dotnet run --project tools/SampleBillGenerator -- samples
```

## Conventions

- Nullable, implicit usings, **warnings as errors** (`api/Directory.Build.props`).
- Options bound with `ValidateOnStart` — a missing model name fails at boot, not on first request.
- `CancellationToken` threaded through every async path.
- No provider SDK types outside `Infrastructure/Ai/`.
- Endpoints stay thin: validate, call a service, map to a DTO.
- Tests never require Ollama or Qdrant. The ones that do use `[IntegrationFact]`, which skips unless
  `BILLS_INTEGRATION=1`, and carry `[Trait("Category","Integration")]`.

## Gotchas

These are the things that actually cost time here.

- **IPv6 breaks .NET networking on this machine.** `dotnet restore` fails with "The SSL connection
  could not be established / Broken pipe", and so does any outbound HTTPS from .NET, because IPv6 is
  black-holed and .NET tries it first (curl silently falls back to IPv4; .NET does not). Prefix .NET
  commands with `DOTNET_SYSTEM_NET_DISABLEIPV6=1`.
- **`npm install` fails** with `Cannot read properties of null (reading 'edgesOut')` — an npm 11.4
  bug resolving the vitest peer graph. Use `npm install --legacy-peer-deps`.
- **Qdrant's .NET client speaks gRPC on 6334**, not the REST port 6333. A container that publishes
  only 6333 gives "Connection refused" from the API while `curl localhost:6333` looks perfectly
  healthy. The compose file publishes both. A restarted Qdrant also needs the API restarted, because
  the gRPC channel caches its failed subchannel.
- **Embedding dimensions are load-bearing.** nomic-embed-text is 768-d, Azure text-embedding-3-small
  is 1536-d. Switching providers invalidates the collection: drop it and re-ingest. The collection
  name embeds the model (`bill_chunks_nomic_embed_text`) so a mismatch surfaces as "collection not
  found" instead of nonsense results. `/health` also compares configured against actual dimensions.
- **Ollama's default context window is 4096 tokens**, whatever the model advertises. An over-long
  prompt is truncated from the *front* — dropping the system instructions while keeping the excerpts,
  which yields an ungrounded answer that looks fine. `OllamaDefaultsChatClient` sets `num_ctx`, and
  top-K is capped at 12.
- **llama3.2 needs to be told to fill every field.** Asked plainly, it returns the two or three fields
  it happened to notice and omits the rest. The prompt in `Core/Prompts/BillPrompts.cs` earns its
  length: where each fact is printed, an explicit demand for all ten keys, and one worked example.
  Removing any of that regresses extraction — check against every PDF in `samples/` before editing it.
- **SQLite cannot sum a `decimal` or sort a `DateTimeOffset`.** Money is therefore stored as
  `AmountDueMinor` (integer cents, with `Bill.AmountDue` as an unmapped facade) and `UploadedAt` is
  converted to UTC ticks. Both exist so the aggregates stay exact *and* translate to SQL.
- **Vector payloads take only primitive types.** `BillChunk.BillId` is a `string`, not a `Guid`: a
  Guid is fine as a point key but rejected as a filterable data property.
- **PdfPig is text-only** and cannot read scanned bills. `PdfPigTextExtractor` detects a near-empty
  text layer and throws `UnreadablePdfException`, which the endpoint turns into a 400 explaining why.
- **Use `ContentOrderTextExtractor`, never `page.Text`.** The latter returns glyphs in content-stream
  order, which interleaves the address block with the charges table on a two-column bill.
- Chunk with overlap and never split mid-line. Utility bills keep the figures that matter in rate
  tables, and a boundary through a table row strands a number from its label — which reads as
  authoritative to the model and produces confidently wrong answers. `BillChunker` keeps table blocks
  whole where they fit and repeats the header when it must split one.
