# adPlay: ASP.NET Core & MySQL Written Test

Answers to the *ASP.NET Core & MySQL Software Engineer* written test, plus a working Web API that implements them.

- **Answers document:** [docs/ANSWERS.pdf](docs/ANSWERS.pdf), all 15 questions with explanations (PDF).
- **SQL (Section A):** [sql/](sql)
- **API (Sections B–D):** [src/AdPlay.Api](src/AdPlay.Api)
- **Tests:** [tests/AdPlay.Tests](tests/AdPlay.Tests)

## Where each question lives

| Q | Topic | Code |
|---|---|---|
| 1–4 | Top-N per group, login streaks, window analytics, sargable rewrite | [sql/02_section_a_queries.sql](sql/02_section_a_queries.sql) |
| 5 | Secure dynamic search stored procedure | [sql/03_sp_search_orders.sql](sql/03_sp_search_orders.sql) |
| 6 | Duplicate-subscription prevention | [Features/Subscriptions](src/AdPlay.Api/Features/Subscriptions) |
| 7 | Idempotent payments, webhooks, reconciliation | [Features/Payments](src/AdPlay.Api/Features/Payments) |
| 8 | Spec-based read repository | [Common/Querying/QuerySpec.cs](src/AdPlay.Api/Common/Querying/QuerySpec.cs), [ReadRepository.cs](src/AdPlay.Api/Common/Querying/ReadRepository.cs) |
| 9 | `IQueryable.OrderBy("Salary desc")` | [Common/Querying/QueryableSortExtensions.cs](src/AdPlay.Api/Common/Querying/QueryableSortExtensions.cs) |
| 10 | Logging middleware with redaction | [Middleware](src/AdPlay.Api/Middleware) |
| 11 | Reliability & observability plan | Written answer only, in [docs/ANSWERS.pdf](docs/ANSWERS.pdf) |
| 12–13 | LINQ order summary, salary statistics | [Features/Analytics](src/AdPlay.Api/Features/Analytics) |
| 14–15 | Product search, keyset pagination, caching | [Features/Products](src/AdPlay.Api/Features/Products) |

## Run it

Requires the .NET 10 SDK and Docker.

```bash
docker compose up -d
```

```bash
dotnet run --project src/AdPlay.Api
```

In Development the API creates and seeds the schema on startup. OpenAPI is served at `/openapi/v1.json`.

Example calls:

```bash
curl "http://localhost:5165/api/products?keyword=Phone&sortBy=price&sortDirection=asc&page=1&pageSize=5"
```

```bash
curl -X POST http://localhost:5165/api/payments -H "Content-Type: application/json" -H "X-Merchant-Id: m_1" -H "Idempotency-Key: order-1001-attempt-1" -d '{"orderId":"1001","amount":250.00,"currency":"BDT","paymentMethodToken":"tok_demo"}'
```

(Port 5165 comes from `src/AdPlay.Api/Properties/launchSettings.json`.)

## Tests

```bash
dotnet test
```

Tests use SQLite files so they run without MySQL. They cover concurrent subscription and payment requests, idempotent replay, webhook dedupe, reconciliation after timeouts, keyset pagination, dynamic sorting validation, redaction and the middleware pipeline (via `TestServer`).
