# File Processing API

A secure ASP.NET Core web service that accepts uploaded CSV transaction files, validates and
aggregates them, tracks every file it has processed, and reports on that activity.

- **Format:** CSV
- **Aggregate:** total, count and **average** amount per file and per category, plus totals by
  currency and the transaction date range
- **Security:** API key in the `X-Api-Key` header, with scope-based authorization
- **Docs:** live OpenAPI at `/swagger`

---

## Contents

- [Prerequisites](#prerequisites)
- [Building](#building)
- [Running](#running)
- [Testing](#testing)
- [Authentication](#authentication)
- [API endpoints](#api-endpoints)
- [CSV file format](#csv-file-format)
- [File tracking](#file-tracking)
- [Configuration](#configuration)

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — only needed to build outside a container
- PostgreSQL 14+ — or just Docker, which supplies it

---

## Building

```bash
dotnet restore
dotnet build -c Release
```

Warnings are treated as errors, so a clean build is a real gate.

To publish a self-contained output folder:

```bash
dotnet publish src/FileProcessing.Api -c Release -o ./publish
```

To build the container image (from the **repository root** — the build needs
`Directory.Build.props` and the referenced projects):

```bash
docker build -f src/FileProcessing.Api/Dockerfile -t fileprocessing-api .
```

---

## Running

### With Docker Compose — one command, nothing else needed

```bash
docker compose up --build
```

Compose starts PostgreSQL, waits for it to accept connections, then starts the API, which applies
its migrations on start-up.

- API: <http://localhost:5080>
- Swagger UI: <http://localhost:5080/swagger>

```bash
docker compose logs -f api      # follow the API log
docker compose down -v          # stop and drop the database volume
```

### Against a local PostgreSQL

Create the database and role:

```bash
psql -U postgres -c "CREATE ROLE fileprocessing LOGIN PASSWORD 'fileprocessing'; CREATE DATABASE fileprocessing OWNER fileprocessing;"
```

Then run:

```bash
dotnet run --project src/FileProcessing.Api
```

To point at different credentials without editing any file:

```bash
dotnet user-secrets --project src/FileProcessing.Api set "ConnectionStrings:FileProcessingDb" "Host=localhost;Port=5432;Database=fileprocessing;Username=postgres;Password=yourpassword"
```

In `Development` the service migrates on start-up. If the database is unreachable it logs a
critical error and keeps running, so Swagger and `/health/ready` still come up and say what is
wrong rather than the process dying silently.

### Running the published output or the image directly

```bash
dotnet ./publish/FileProcessing.Api.dll
```

```bash
docker run --rm -p 5080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e ConnectionStrings__FileProcessingDb="Host=host.docker.internal;Port=5432;Database=fileprocessing;Username=fileprocessing;Password=fileprocessing" \
  fileprocessing-api
```

### Applying the schema by hand

Production does not auto-migrate. Either run the generated script:

```bash
psql -U fileprocessing -d fileprocessing -f database/schema.sql
```

or use the EF tooling:

```bash
dotnet ef database update --project src/FileProcessing.Infrastructure --startup-project src/FileProcessing.Api
```

---

## Testing

```bash
dotnet test
```

121 tests — 87 unit, 34 integration. No database and no Docker required; run it straight after
cloning. The integration tests host the real application through `WebApplicationFactory` with the
database swapped for an in-memory provider.

```bash
dotnet test -v normal                                          # show each test
dotnet test --filter "FullyQualifiedName~ApiKeyAuthenticationTests"
```

---

## Authentication

Every endpoint except the health probes requires an API key in the `X-Api-Key` header. Endpoints
are protected by default and must opt out explicitly.

Configuration stores only the **SHA-256 digest** of a key, never the key itself.

### Development keys

These are configured in `appsettings.Development.json` so the service can be run and reviewed with
no setup.

| Key | Client | Scopes |
|---|---|---|
| `dev-uploader-key-4f2b8c1e9a7d3506` | `dummy-freight` | `files:write`, `files:read`, `reports:read` |
| `dev-readonly-key-91c7ae2d5b0846f3` | `beta-logistics` | `files:read`, `reports:read` |
| `dev-ops-key-7b3e5f9c2a418d60` | `operations` | `files:read:all`, `reports:read` |

### Scopes

| Scope | Grants |
|---|---|
| `files:write` | Upload and process files |
| `files:read` | Read your own processed files |
| `files:read:all` | Read every client's files |
| `reports:read` | Read the summary report |

A client sees only its own uploads unless its key carries `files:read:all`.

### Issuing a new key

```bash
KEY=$(openssl rand -base64 32) && echo "key:    $KEY" && echo "digest: $(printf '%s' "$KEY" | sha256sum | cut -d' ' -f1)"
```

Put the digest in configuration and give the key to the client. Supply it through environment
variables outside development:

```bash
export Authentication__ApiKey__Clients__0__ClientId=dummy-freight
export Authentication__ApiKey__Clients__0__KeySha256=<digest>
export Authentication__ApiKey__Clients__0__Scopes__0=files:write
```

---

## API endpoints

Base URL `http://localhost:5080`. All responses are JSON; errors are RFC 7807
`application/problem+json` and carry a `correlationId` matching the `X-Correlation-Id` response
header. Swagger UI at `/swagger` lets you set a key once and call everything from the browser.

| Method | Route | Scope | Purpose |
|---|---|---|---|
| `POST` | `/api/v1/files` | `files:write` | Upload and process a CSV |
| `GET` | `/api/v1/files` | `files:read` | List tracked files (filter + page) |
| `GET` | `/api/v1/files/{id}` | `files:read` | One file with its row errors |
| `GET` | `/api/v1/reports/summary` | `reports:read` | Aggregate processing report |
| `GET` | `/health/live` | none | Liveness |
| `GET` | `/health/ready` | none | Readiness, including the database |

### Status codes

| Code | When |
|---|---|
| `201` | Processed. Body carries the aggregates and any row errors; `Location` points at the record. |
| `400` | No file part, an empty file, or invalid query parameters. |
| `401` | Missing, malformed or unrecognised API key. |
| `403` | Valid key, but it lacks the scope this endpoint needs. |
| `404` | No such file, or it belongs to another client. |
| `409` | Identical content already processed (when duplicate rejection is enabled). |
| `413` | Upload above the configured size limit. |
| `415` | File extension or content type not accepted. |
| `422` | The file could not be processed at all — bad header, not CSV, no data rows. |
| `429` | Rate limit for this API key exceeded. |

---

### `POST /api/v1/files`

Uploads a CSV, processes it, records it, and returns the aggregates.

**Request** — `multipart/form-data` with one part named `file`.

| Part | Required | Notes |
|---|---|---|
| `file` | yes | Extension must be `.csv`. Content type must be one of `text/csv`, `application/csv`, `text/plain`, `application/vnd.ms-excel`, `application/octet-stream`. 10 MB default limit. |

```bash
curl -X POST http://localhost:5080/api/v1/files \
  -H "X-Api-Key: dev-uploader-key-4f2b8c1e9a7d3506" \
  -F "file=@samples/transactions-valid.csv;type=text/csv"
```

**`201 Created`**

```json
{
  "fileId": "0f7a1c94-8d2e-4b31-9a76-1f5c2e3d4b58",
  "fileName": "transactions-valid.csv",
  "status": "Succeeded",
  "receivedAtUtc": "2026-08-28T05:41:12.4180000+00:00",
  "completedAtUtc": "2026-08-28T05:41:12.4250000+00:00",
  "durationMilliseconds": 7,
  "sizeBytes": 612,
  "sha256": "9d5e0c1b7a3f...",
  "rows": { "total": 10, "valid": 10, "invalid": 0 },
  "aggregates": {
    "totalAmount": 3721.85,
    "averageAmount": 372.19,
    "totalsByCurrency": { "AUD": 3111.85, "NZD": 610.00 },
    "byCategory": [
      { "category": "Linehaul", "count": 3, "totalAmount": 3040.25, "averageAmount": 1013.42 },
      { "category": "Warehousing", "count": 1, "totalAmount": 320.00, "averageAmount": 320.00 }
    ],
    "earliestTransactionDate": "2026-07-01",
    "latestTransactionDate": "2026-07-22"
  },
  "errors": [],
  "errorsTruncated": false
}
```

| Field | Meaning |
|---|---|
| `status` | `Succeeded`, `CompletedWithErrors` or `Failed` |
| `rows` | Data rows seen, accepted and rejected |
| `aggregates.averageAmount` | Mean amount over the **valid** rows, to 2 dp |
| `aggregates.byCategory` | Count, total and average per category, largest total first |
| `errors` | One entry per rejected row: `line`, `field`, `code`, `message` |
| `errorsTruncated` | `true` when more rows failed than the retained-error cap (100 by default) |

A file with some bad rows is **not** an error — it is `201` with status `CompletedWithErrors` and
aggregates over the rows that passed:

```bash
curl -X POST http://localhost:5080/api/v1/files \
  -H "X-Api-Key: dev-uploader-key-4f2b8c1e9a7d3506" \
  -F "file=@samples/transactions-with-errors.csv;type=text/csv"
```

```json
{
  "status": "CompletedWithErrors",
  "rows": { "total": 10, "valid": 2, "invalid": 8 },
  "aggregates": { "totalAmount": 775.50, "averageAmount": 387.75 },
  "errors": [
    {
      "line": 3,
      "field": "TransactionDate",
      "code": "transactionDate.invalid_format",
      "message": "Transaction date must be an ISO 8601 date in the form yyyy-MM-dd."
    },
    {
      "line": 9,
      "field": null,
      "code": "row.column_count_mismatch",
      "message": "Expected 6 columns but found 3."
    }
  ]
}
```

A file that cannot be processed at all is `422`, and is still tracked:

```json
{
  "title": "File could not be processed",
  "status": 422,
  "detail": "The header is missing required columns: TransactionId, Currency, Category.",
  "fileId": "3b1d...",
  "correlationId": "9c904b90ab9748fd8703ce117de22703"
}
```

---

### `GET /api/v1/files`

Lists tracked files, newest first.

| Query parameter | Type | Default | Notes |
|---|---|---|---|
| `status` | enum | — | `Pending`, `Succeeded`, `CompletedWithErrors`, `Failed` |
| `receivedFrom` | ISO 8601 instant | — | Inclusive lower bound |
| `receivedTo` | ISO 8601 instant | — | Inclusive upper bound; must not precede `receivedFrom` |
| `fileName` | string | — | Case-insensitive substring match |
| `page` | int | `1` | 1-based |
| `pageSize` | int | `25` | Capped server side at 200 |

```bash
curl "http://localhost:5080/api/v1/files?status=Succeeded&pageSize=10" \
  -H "X-Api-Key: dev-uploader-key-4f2b8c1e9a7d3506"
```

```json
{
  "items": [
    {
      "fileId": "0f7a1c94-8d2e-4b31-9a76-1f5c2e3d4b58",
      "fileName": "transactions-valid.csv",
      "clientId": "dummy-freight",
      "status": "Succeeded",
      "receivedAtUtc": "2026-08-28T05:41:12.4180000+00:00",
      "durationMilliseconds": 7,
      "sizeBytes": 612,
      "sha256": "9d5e0c1b7a3f...",
      "rows": { "total": 10, "valid": 10, "invalid": 0 },
      "totalAmount": 3721.85
    }
  ],
  "page": 1,
  "pageSize": 10,
  "totalCount": 1,
  "totalPages": 1,
  "hasNextPage": false
}
```

---

### `GET /api/v1/files/{id}`

One tracked file with its retained row errors. A file belonging to another client returns `404`.

```bash
curl http://localhost:5080/api/v1/files/0f7a1c94-8d2e-4b31-9a76-1f5c2e3d4b58 \
  -H "X-Api-Key: dev-uploader-key-4f2b8c1e9a7d3506"
```

```json
{
  "file": { "fileId": "0f7a...", "fileName": "transactions-valid.csv", "status": "Succeeded" },
  "errors": [],
  "errorsTruncated": false
}
```

---

### `GET /api/v1/reports/summary`

Aggregate reporting across everything the caller has processed, optionally over a date window.

| Query parameter | Type | Notes |
|---|---|---|
| `from` | ISO 8601 instant | Inclusive lower bound on receipt time |
| `to` | ISO 8601 instant | Inclusive upper bound; must not precede `from` |

```bash
curl "http://localhost:5080/api/v1/reports/summary?from=2026-08-01T00:00:00Z" \
  -H "X-Api-Key: dev-uploader-key-4f2b8c1e9a7d3506"
```

```json
{
  "fromUtc": "2026-08-01T00:00:00+00:00",
  "totalFiles": 4,
  "succeededFiles": 2,
  "filesWithErrors": 1,
  "failedFiles": 1,
  "totalBytes": 2481,
  "rows": { "total": 30, "valid": 22, "invalid": 8 },
  "totalAmount": 8219.20,
  "averageRowAmount": 373.60,
  "averageDurationMilliseconds": 6.25,
  "firstReceivedAtUtc": "2026-08-28T05:41:12.4180000+00:00",
  "lastReceivedAtUtc": "2026-08-28T05:48:03.1020000+00:00",
  "byClient": [
    { "clientId": "dummy-freight", "fileCount": 4, "totalRows": 30, "totalAmount": 8219.20 }
  ]
}
```

---

### `GET /health/live` and `GET /health/ready`

Anonymous. `live` answers "is the process up"; `ready` additionally proves the database is
reachable and returns `503` when it is not.

```bash
curl -i http://localhost:5080/health/ready
```

---

## CSV file format

Header row required. Column order and casing do not matter. `Description` is optional; the rest are
required.

| Column | Rules |
|---|---|
| `TransactionId` | Required, ≤ 64 chars, unique within the file |
| `TransactionDate` | Required, ISO 8601 `yyyy-MM-dd`, not in the future |
| `Description` | Optional, ≤ 256 chars, no control characters |
| `Amount` | Required decimal, ≤ 2 decimal places, thousands separators and negatives accepted |
| `Currency` | Required, three-letter ISO 4217 code |
| `Category` | Required, ≤ 64 chars |

Quoted fields, escaped quotes, embedded commas, embedded newlines and a UTF-8 BOM are all handled.

```csv
TransactionId,TransactionDate,Description,Amount,Currency,Category
TXN-1001,2026-07-01,Melbourne to Geelong linehaul,1450.00,AUD,Linehaul
TXN-1002,2026-07-01,"Fuel levy, July",212.55,AUD,Fuel
```

Sample files are in `samples/`: a clean one, one exercising every validation failure, and one with
an unusable header. The figures shown above for those files are asserted by `SampleFileTests`.

### Validation error codes

Every rejected row carries a stable code, so a client can branch on it without parsing prose.

| Code | Meaning |
|---|---|
| `transactionId.missing` / `transactionId.too_long` | Id absent, or over 64 characters |
| `transactionId.duplicate` | Id already used earlier in the same file |
| `transactionDate.missing` / `transactionDate.invalid_format` | Absent, or not `yyyy-MM-dd` |
| `transactionDate.in_future` | Dated ahead of today (one day of slack for time zones) |
| `description.too_long` / `description.invalid_characters` | Over 256 characters, or contains control characters |
| `amount.missing` / `amount.not_a_number` | Absent, or not parseable as a decimal |
| `amount.too_many_decimals` / `amount.out_of_range` | More than 2 dp, or outside the accepted range |
| `currency.missing` / `currency.invalid_format` | Absent, or not a three-letter code |
| `category.missing` / `category.too_long` | Absent, or over 64 characters |
| `row.column_count_mismatch` | Row has a different number of columns than the header |
| `file.malformed_csv` | The bytes are not well-formed CSV (file-level, not row-level) |

---

## File tracking

Every upload that passes authentication is recorded — whether it succeeded, partly failed, was
rejected before processing, or was a duplicate. Tracking is a durable PostgreSQL table rather than
an in-process counter, so it survives a restart and works behind more than one instance.

The record is written **before** processing starts and updated when it finishes, so a process that
dies mid-file still leaves evidence the file arrived.

### What is recorded

| Field | Notes |
|---|---|
| `id` | Identifier returned to the caller and used to fetch the record later |
| `fileName` | Sanitised original name |
| `contentType` | As declared on the multipart section |
| `clientId` | The authenticated API client — this is what scopes the read side |
| `receivedAtUtc` / `completedAtUtc` | When it arrived and when processing finished |
| `durationMilliseconds` | Wall-clock processing time |
| `status` | `Pending`, `Succeeded`, `CompletedWithErrors`, `Failed` |
| `sizeBytes` | Bytes actually read, not the declared `Content-Length` |
| `sha256` | Digest of the content, for de-duplication and audit |
| `totalRows` / `validRows` / `invalidRows` | Row tallies |
| `totalAmount` | Sum over the valid rows |
| `failureReason` | Set when the file could not be processed |
| `errorsTruncated` | Whether more rows failed than the retention cap |

Row-level errors are stored in a child table, capped at 100 per file by default.

Each upload also logs a structured line stamped with the request's correlation id:

```
info: Processed file 0f7a1c94-… for dummy-freight: Succeeded, 10/10 rows valid in 7ms.
```

### Reading it back

Use [`GET /api/v1/files`](#get-apiv1files) to list records,
[`GET /api/v1/files/{id}`](#get-apiv1filesid) for one with its row errors, and
[`GET /api/v1/reports/summary`](#get-apiv1reportssummary) for the aggregate view.

---

## Configuration

Every setting can be supplied by environment variable using `__` as the separator — for example
`FileProcessing__MaxFileSizeInBytes`.

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings:FileProcessingDb` | — | PostgreSQL connection string (required) |
| `FileProcessing:MaxFileSizeInBytes` | `10485760` | Upload size limit |
| `FileProcessing:MaxRows` | `100000` | Largest number of data rows accepted |
| `FileProcessing:MaxRetainedErrors` | `100` | Row errors stored and returned per file |
| `FileProcessing:AllowedExtensions` | `[".csv"]` | Accepted file extensions |
| `FileProcessing:AllowedContentTypes` | see `appsettings.json` | Accepted content types |
| `FileProcessing:RejectDuplicateUploads` | `false` | Reject re-uploading identical content |
| `RateLimiting:UploadPermitLimit` | `20` | Uploads per client per window |
| `RateLimiting:UploadWindowSeconds` | `60` | Upload window length |
| `RateLimiting:GlobalPermitLimit` | `300` | Requests per client per window, all endpoints |
| `Authentication:ApiKey:HeaderName` | `X-Api-Key` | Header carrying the key |
| `Authentication:ApiKey:Clients` | — | Client list; each has `ClientId`, `KeySha256`, `Scopes` |
