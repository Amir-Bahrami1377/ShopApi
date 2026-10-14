# Product and Order API

An ASP.NET Core 10 controller-based API for products, stock reservations, orders, and a simulated payment flow. It uses EF Core 10 with SQL Server and is designed to keep inventory and idempotency correct when requests race.

## Quick start with Docker

Docker Compose starts SQL Server, waits for its health check, then starts the API. The API applies committed migrations and inserts the fixed demo products on startup.

```bash
docker compose up --build -d
docker compose ps
curl -i http://localhost:8080/health
```

Interactive API documentation is available through Scalar at
`http://localhost:8080/scalar` while the Compose development stack is running.
The underlying OpenAPI document is available at `http://localhost:8080/openapi/v1.json`.

Follow startup logs if the API is still applying its first migration:

```bash
docker compose logs -f api
```

The defaults are intended only for local development. Override the SQL Server password before using this setup anywhere else:

```bash
MSSQL_SA_PASSWORD='A-different-strong-password1!' docker compose up --build -d
```

The same value is interpolated into both the SQL Server container and the API connection string. `API_PORT`, `SQL_PORT`, `PAYMENT_MODE`, `PAYMENT_DELAY_MS`, and `PAYMENT_TIMEOUT_SECONDS` can also be overridden. The payment mode is one of `Success`, `Declined`, `Error`, `Timeout`, or `Random`.

Stop the stack with `docker compose down`. To also delete the development database volume and restore the original seed stock the next time it starts, run:

```bash
docker compose down -v
```

## Run locally

Requirements are the .NET 10 SDK and a reachable SQL Server. One convenient option is to run only the Compose database:

```bash
docker compose up -d db
dotnet restore ProductOrderApi.slnx
dotnet run --project ProductOrderApi.csproj
```

The local launch profile serves the API at `http://localhost:5080`. Its development connection string uses SQL Server on `localhost:1433`, database `ProductOrderDb`, user `sa`, and password `YourStrong!Passw0rd`. Override `ConnectionStrings__DefaultConnection` rather than committing a real credential. Startup migration and seeding can be toggled with `Database__MigrateOnStartup` and `Database__SeedOnStartup`.

For example, in PowerShell:

```powershell
$env:ConnectionStrings__DefaultConnection = 'Server=localhost,1433;Database=ProductOrderDb;User Id=sa;Password=YourStrong!Passw0rd;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
dotnet run --project ProductOrderApi.csproj
```

## Run the tests

The integration tests deliberately use real SQL Server; they do not use EF's InMemory provider or SQLite. Start the Compose database and point `TEST_DB` at it:

```powershell
docker compose up -d db
$env:TEST_DB = 'Server=localhost,1433;Database=ProductOrderApiTests;User Id=sa;Password=YourStrong!Passw0rd;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
dotnet test ProductOrderApi.slnx
```

The equivalent Bash setup is:

```bash
docker compose up -d db
export TEST_DB='Server=localhost,1433;Database=ProductOrderApiTests;User Id=sa;Password=YourStrong!Passw0rd;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
dotnet test ProductOrderApi.slnx
```

Tests create isolated databases derived from the configured database name. The suite covers readiness and unknown-route errors, concurrent stock reservation, duplicate-line normalization, sequential and concurrent idempotency replays, confirmation races and disconnects, all payment outcomes, cancellation, validation, missing resources, and idempotent seeding.

## API

JSON uses camel-case property names. Failures use `application/problem+json`; validation failures include an `errors` object and every error response includes a `traceId`. Unknown routes use the same Problem Details format instead of an empty `404` response. The health endpoint checks SQL Server connectivity asynchronously, so it reports readiness rather than only process liveness.

| Method | Route | Success | Other expected statuses |
| --- | --- | --- | --- |
| `GET` | `/health` | `200` | `503` database unavailable |
| `POST` | `/products` | `201` with `Location` | `400` validation |
| `GET` | `/products?page=1&pageSize=20` | `200` paged result | `400` invalid paging; maximum page size is 100 |
| `GET` | `/products/{id}` | `200` | `404` missing product |
| `POST` | `/orders` | `201` new order; `200` replay | `400` validation/key too long, `404` product missing, `409` insufficient stock, `422` key reused with another body |
| `GET` | `/orders/{id}` | `200` | `404` missing order |
| `POST` | `/orders/{id}/confirm` | `200` | `402` declined, `404` missing, `409` invalid/concurrent transition, `502` provider error, `504` timeout |
| `POST` | `/orders/{id}/cancel` | `200` | `404` missing, `409` confirmed or payment-processing order |

Route IDs are GUIDs. An order response contains `id`, string `status`, `createdAt`, `total`, and `items` with `productId`, `quantity`, and the snapshotted `unitPrice`.

### Seed products

Seeding is idempotent: only a missing fixed ID is inserted, so restarting the API never resets changed stock.

| ID | Name | Price | Initial stock |
| --- | --- | ---: | ---: |
| `11111111-1111-1111-1111-111111111111` | Concurrency Widget | 9.99 | 1 |
| `22222222-2222-2222-2222-222222222222` | Standard Widget | 19.99 | 100 |
| `33333333-3333-3333-3333-333333333333` | Premium Widget | 49.99 | 25 |
| `44444444-4444-4444-4444-444444444444` | Out of Stock Widget | 5.00 | 0 |

### Example requests

Create and list products:

```bash
curl -i -X POST http://localhost:8080/products \
  -H 'Content-Type: application/json' \
  --data '{"name":"Desk Lamp","price":24.95,"stock":8}'

curl -sS 'http://localhost:8080/products?page=1&pageSize=20'
```

Create an order. Repeating this exact request and key returns the original order with status `200` and header `Idempotent-Replayed: true`; the first request returns `201` and decrements stock once.

```bash
curl -i -X POST http://localhost:8080/orders \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: readme-order-001' \
  --data '{"items":[{"productId":"22222222-2222-2222-2222-222222222222","quantity":2}]}'
```

Use the returned order ID to read, confirm, or cancel it:

```bash
curl -sS http://localhost:8080/orders/ORDER_ID
curl -i -X POST http://localhost:8080/orders/ORDER_ID/confirm
curl -i -X POST http://localhost:8080/orders/ANOTHER_PENDING_ORDER_ID/cancel
```

### Concurrency demo

Start from a fresh volume because the concurrency seed has exactly one unit. This Bash example sends ten requests at once; distinct idempotency keys ensure they are ten distinct order attempts.

```bash
docker compose down -v
docker compose up --build -d
until curl -fsS http://localhost:8080/health >/dev/null; do sleep 2; done

seq 1 10 | xargs -P 10 -I {} \
  curl -sS -o /dev/null -w '%{http_code}\n' \
  -X POST http://localhost:8080/orders \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: concurrency-demo-{}' \
  --data '{"items":[{"productId":"11111111-1111-1111-1111-111111111111","quantity":1}]}' \
  | sort | uniq -c
```

The result is one `201` and nine `409` responses. The product then has stock zero:

```bash
curl -sS http://localhost:8080/products/11111111-1111-1111-1111-111111111111
```

## Technical decisions

- **Atomic inventory:** order lines are merged by product ID and sorted before locks are taken. Each reservation is a conditional SQL update (`Stock = Stock - quantity WHERE Stock >= quantity`) inside the order transaction. Zero affected rows means `409`, the whole order rolls back, and a database check constraint independently prevents negative stock. Unit prices are copied to order items at reservation time.
- **Durable idempotency:** an order stores the optional key and a SHA-256 hash of a canonicalized request. A filtered unique SQL index enforces one order per key. The key and order commit together. The same body replays the stored order; another body returns `422`. Unique-index races and stock-conflict races roll back before looking up and replaying the winner.
- **Safe confirmation:** a conditional update claims `Pending -> PaymentProcessing`, preventing concurrent charges. The payment call runs without a database transaction or lock held and uses the order ID as the provider idempotency key. Decline, timeout, provider failure, or cancellation while payment is still in flight restores `Pending`. Once the provider returns success, the order always finalizes as `Confirmed` with `CancellationToken.None`, even if the client has disconnected.
- **Cancellation:** only a pending order can be atomically claimed for cancellation. Its stock is restored in the same transaction. Repeated cancellation is successful without adding stock twice; confirmed and payment-processing orders return `409`.
- **Consistent API errors:** controllers only bind and delegate. Validation and domain failures flow through centralized RFC 7807 Problem Details handling; unexpected details are logged but never returned to clients.

## Limitations and next steps

- A process crash after claiming payment can leave an order in `PaymentProcessing`; production needs a reconciliation job that queries the provider using the order ID.
- Pending stock reservations have no expiry. An expiry job should cancel abandoned orders and release their stock.
- A real payment client needs bounded retries with backoff, a circuit breaker, observability, and provider-specific reconciliation.
- Idempotency keys never expire and are not scoped to a customer or tenant.
- There is no authentication, authorization, rate limiting, distributed tracing, or production secret store.
- This sample runs a single SQL Server database and applies migrations during startup. Larger deployments generally run migrations as a controlled deployment step and design for database resiliency/backup.
