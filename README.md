# Transcription Pipeline

A small, real (not mocked-out) audio transcription service built in C# / ASP.NET Core
(.NET 10), covering the full assessment scope: upload, format normalization, chunking
for long files, timestamped segments, persistence, concurrency, and retries.

## Stack and why

- **ASP.NET Core minimal API** - the API surface is two endpoints (`Endpoints/TranscriptionEndpoints.cs`);
  a full MVC controller setup would be unnecessary ceremony.
- **API key authentication** (`Security/`) - both endpoints require an `X-Api-Key`
  header, checked against a server-configured key using a constant-time comparison.
  The key comes from configuration (environment variable / user-secrets in
  development, Key Vault in production), never hard-coded; if it isn't configured at
  all, auth fails closed rather than silently allowing requests through.
- **EF Core + SQLite** - a real relational store for job/segment metadata, with zero
  external infra to stand up for this assessment. Swapping the connection string for
  SQL Server (my usual production target) is a one-line change - the schema and
  queries don't change.
- **ffmpeg / ffprobe** (shelled out via `Process`) - used for format normalization,
  duration probing, and chunking. Reimplementing audio decoding from scratch would be
  solving the wrong problem; ffmpeg is the standard tool for this.
- **Polly** - retry with exponential backoff around each transcription call.
- **System.Threading.Channels** - an in-memory, thread-safe job queue consumed by
  multiple background workers, so uploads don't block each other.
- **Microsoft.CognitiveServices.Speech (Azure Speech SDK)** - the production engine,
  behind an interface, selectable via config. No credentials are required to run this
  project, because...
- **A Mock transcription engine is the default** - per the assessment's own note
  ("mock data where needed... the focus is on engineering decisions, not training a
  model from scratch"), the default engine doesn't call any paid API. It still reads
  the *real* duration of each chunk via ffprobe and emits evenly-spaced placeholder
  segments, so every other part of the pipeline (upload, normalization, chunking,
  offsetting, retries, persistence) runs exactly as it would with a real STT call.
  `AzureSpeechTranscriptionEngine` is a complete, real implementation behind the same
  `ITranscriptionEngine` interface - set `"TranscriptionEngine": "AzureSpeech"` and
  `Speech:SubscriptionKey` / `Speech:Region` (env vars, not committed) to use it.

## How it works

```
POST /api/transcribe (multipart file)
  -> validate magic bytes (WAV/MP3), reject anything else
  -> save to local disk storage (IAudioStorage - swap for Blob/S3 in production)
  -> create Job row (Status=Pending), return 202 + jobId immediately
  -> enqueue jobId onto an in-memory Channel

Background workers (N concurrent, Worker:Concurrency, default 2)
  -> dequeue a jobId
  -> normalize audio to 16kHz mono PCM WAV via ffmpeg (handles format differences)
  -> if duration > Chunking:ThresholdSeconds (120s default):
       split into overlapping chunks (ChunkLengthSeconds=60, OverlapSeconds=5)
     else: treat the whole file as one chunk
  -> transcribe each chunk via ITranscriptionEngine, wrapped in a Polly retry
     policy (3 attempts, exponential backoff)
  -> offset each chunk's segment timestamps by the chunk's start time, save to DB
  -> mark Job Completed, or Failed with the error message if retries are exhausted

GET /api/transcribe/{id}
  -> returns job status + ordered segments (start/end/text)
```

## Design decisions (direct answers to the assessment questions)

**How do you handle different audio formats?**
Normalize everything to one canonical format (16kHz mono PCM WAV) via ffmpeg before
anything else touches it. This keeps every downstream step - chunking, duration
probing, transcription - format-agnostic; format-specific quirks are isolated to a
single conversion step instead of being handled throughout the pipeline.

**How do you deal with long audio files?**
Files under the threshold (120s by default) are transcribed in one pass. Longer files
are split into overlapping chunks (60s, 5s overlap) so no single transcription call has
to hold an entire long file, and chunks could be processed in parallel. Overlap exists
so words aren't lost at a hard cut boundary.
*Known simplification:* this implementation offsets timestamps correctly across chunks
but does not de-duplicate the text in the overlapping regions - you can see this in the
output as a short run of segments that repeats near each chunk boundary. A production
version would either align/diff the overlapping text or split strictly on detected
silence (VAD) instead of fixed-length windows, so chunk boundaries fall in natural gaps.

**How would you handle concurrent uploads?**
The API never does the transcription work inline - it validates, stores, and enqueues,
then returns `202 Accepted` immediately. Multiple background workers (`Worker:Concurrency`)
consume the same queue concurrently, so several jobs are genuinely transcribed in
parallel rather than queued one-at-a-time behind the API layer. For multi-instance
scale, swapping the in-memory `Channel` for Azure Storage Queue / Service Bus behind
the same `ITranscriptionQueue` interface is the natural next step.

**How would you store audio and transcripts?**
Split by access pattern: large binary audio goes to blob/object storage (`IAudioStorage`
- implemented locally here, Azure Blob/S3 in production), while structured,
queryable data (job status, segments with timestamps) goes into a relational database
(EF Core). Keeping large binaries out of the database and structured data out of blob
storage is the standard split for this kind of workload.

**How do you retry or recover failed transcriptions?**
Each chunk's transcription call is wrapped in a Polly retry pipeline: 3 attempts with
exponential backoff for transient failures (timeouts, rate limits). A job's status
(`Pending` / `Processing` / `Completed` / `Failed`) and `AttemptCount` are tracked and
persisted, and the specific error is stored on the job (`ErrorMessage`) when retries are
exhausted, rather than being silently swallowed - a human or a separate reprocessing job
can act on genuinely failed jobs instead of them disappearing.

**How would you expose this as an API?**
`POST /api/transcribe` to submit a file and get a job ID back immediately (never
blocking on the actual transcription), and `GET /api/transcribe/{id}` to poll status and
retrieve results once ready. This keeps the HTTP request/response cycle short regardless
of how long transcription actually takes; a webhook callback on completion would be the
next addition for a real client instead of polling.

## Running it

Requirements: .NET 10 SDK, `ffmpeg`/`ffprobe` on PATH.

```bash
dotnet run --urls http://localhost:5299
```

Both endpoints require the `X-Api-Key` header. In Development, the key is
`dev-local-only-key-change-me` (set in `appsettings.Development.json`, local-only -
not a real secret); in any other environment, set `Security:ApiKey` via an environment
variable or secret store, or every request gets a 401.

Submit a file:
```bash
curl -X POST http://localhost:5299/api/transcribe -H "X-Api-Key: dev-local-only-key-change-me" -F "file=@sample.mp3"
# => {"jobId":"..."}
```

Poll the result:
```bash
curl http://localhost:5299/api/transcribe/<jobId> -H "X-Api-Key: dev-local-only-key-change-me"
# => { "status": "Completed", "segments": [ { "start": 0, "end": 5, "text": "..." }, ... ] }
```

Verified manually against a 12s MP3, an 8s WAV (confirming both formats are accepted and
normalized), an invalid non-audio file (confirmed rejected with a 400), a 150s WAV
(confirming it correctly splits into multiple overlapping chunks), and both endpoints
with no key / a wrong key / the correct key (confirming 401 / 401 / 200).

## Postman collection

`TranscriptionPipeline.postman_collection.json` has both requests ready to import -
`Submit Transcription` (POST, with a `file` form field to attach) and
`Get Transcription` (GET, by job ID). It uses collection variables (`baseUrl`,
`apiKey`, `jobId`) with the `X-Api-Key` header already wired up, pointing at the
Development key by default.

## Project structure

```
Endpoints/    - the two HTTP endpoints (POST /api/transcribe, GET /api/transcribe/{id})
Security/     - API key authentication (handler + options)
Services/     - audio validation, storage, ffmpeg normalization/chunking, transcription engines, queue
Workers/      - the background worker that consumes the queue and runs the pipeline
Models/       - job/segment entities and response DTOs
Data/         - EF Core DbContext
```
