# Retrieval observability

The operator-facing companion to [SPEC-110](specs/SPEC-110-rag-retrieval.md). The spec says what has
to be emitted and why; this says what it looks like and what to do about it.

Every search is emitted twice, through one record. `LoggerRetrievalTelemetry` writes a structured
log line and records the same call through a `System.Diagnostics.Metrics.Meter`, which is exported
at `GET /metrics`. The log line is what you read when one query behaved oddly. The meter is what a
dashboard reads, and it survives the log level being raised — which is the first thing anyone does
to a log that writes a line per call.

## The line

One per `IRetrievalQuery.Search`. Information for `Success` and `NotReady`; warning for `Failed` and
`TimedOut`.

```
retrieval backend=CosineRetrievalQuery topK=5 results=5 status=Success latencyMs=104.2
retrieval backend=CosineRetrievalQuery topK=5 results=0 status=NotReady latencyMs=0.3
retrieval backend=SqliteVecRetrievalQuery topK=5 results=0 status=TimedOut errorCode=TaskCanceledException latencyMs=100041.7
retrieval backend=SqliteVecRetrievalQuery topK=5 results=0 status=Failed errorCode=SqliteException latencyMs=12.9
```

| Field | Meaning |
|---|---|
| `backend` | The retrieval implementation the composition profile built. Never merge the two into one series — they are a baseline and a candidate, and a merged figure describes neither. |
| `topK` | What the caller asked for, not what came back. The two differ whenever the index holds fewer matches than the request, and only the ask explains the latency. |
| `results` | Hits returned. Zero on every status but `Success`. |
| `status` | `Success`, `NotReady`, `TimedOut` or `Failed`. Only the last is a fault in retrieval itself. |
| `errorCode` | The type name of what went wrong. Absent on `Success` and on `NotReady`. |
| `latencyMs` | Wall-clock of the call, including the query embed it starts with. |

## Reading the statuses

**`NotReady` is the ordinary state before a first index finishes.** It is not an error and does not
belong in an error rate. A steady trickle of it at startup is the system working; a sustained share
of it long afterwards means the index never converged, and the thing to look at is why indexing is
not completing rather than anything in this file.

**`Failed` also covers an index that failed to build.** The refusal is the same exception either
way, so the difference is carried explicitly rather than inferred: a build that failed is reported
as a failure, with `errorCode` naming what actually broke. In practice this is usually the embedding
backend not answering.

**`TimedOut` is a fault in the embedder, not in retrieval.** A search embeds its query text before
it ranks anything, so an embedding backend that stops answering ends the search. Charging it to
whichever retrieval backend happened to be composed would report a regression in the wrong place.
Expect `latencyMs` near the embedder's deadline (`FolderAssistant:Indexing:OllamaTimeoutSeconds`,
600 s by default) with `errorCode=TimeoutException` when this appears; a `TaskCanceledException`
there is a transport's own deadline, which arrives earlier and means the same thing.

## Scraping

`GET /metrics` on the port the app already serves, in Prometheus text format. No collector in
between — one would be a second process to run before any of this is visible.

```yaml
scrape_configs:
  - job_name: folder-assistant
    static_configs:
      - targets: ["localhost:5000"]
```

Two instruments, both tagged `backend` and `status`:

| Instrument | Type | Use |
|---|---|---|
| `retrieval_search_count_total` | counter | Success rate, per backend. Split out `NotReady` before computing one, or startup traffic will drag it down. |
| `retrieval_search_duration_milliseconds` | histogram | Latency percentiles, per backend. This is the number the two backends are being compared on. |

## Turning the log volume down

```json
"Logging": { "LogLevel": { "FolderAssistant.Retrieval.LoggerRetrievalTelemetry": "Warning" } }
```

That drops the per-call success lines and keeps the failures. It does not affect `/metrics`: the
meter records regardless of the logger's level, so the dashboards keep working with logging turned
all the way down.

## Keeping this file true

The lines above are transcribed from `LoggerRetrievalTelemetry.Record`. If a field is renamed there,
change it here in the same commit — a dashboard built on a field that quietly disappeared draws a
flat line, not an error.
