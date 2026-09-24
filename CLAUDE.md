# CLAUDE.md

请使用中文回答

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

XAUAT EduApi is an ASP.NET Core (.NET 10.0) Web API that proxies and scrapes data from Xi'an University of Architecture and Technology's educational administration system, providing standardized RESTful APIs for mobile apps and other clients.

## Build and Run Commands

```bash
# Build the solution
dotnet build

# Run the API (from solution root)
dotnet run --project XAUAT.EduApi

# Run all tests
dotnet test

# Run a specific test class
dotnet test --filter "FullyQualifiedName~CourseServiceTests"

# Run a single test
dotnet test --filter "FullyQualifiedName~CourseServiceTests.GetCourses_ShouldReturnCourses"

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run performance benchmarks
dotnet run -c Release --project Tests/XAUAT.EduApi.Tests.csproj -- --filter "*PerformanceTests*"
```

## Architecture

### Solution Structure

- **XAUAT.EduApi/** - Main ASP.NET Core Web API project
- **EduApi.Data/** - Data access layer (EF Core models, DbContext, migrations)
- **Tests/** - xUnit tests (unit, integration, performance)

### Layered Architecture

1. **Controllers** (`XAUAT.EduApi/Controllers/`) - REST endpoints
2. **Services** (`XAUAT.EduApi/Services/`) - Business logic; call external edu system via HTTP
3. **Repositories** (`XAUAT.EduApi/Repos/`) - Data access with generic repository pattern
4. **Caching** (`XAUAT.EduApi/Caching/`) - Three-tier: local MemoryCache → Redis → database

### Controllers and Their Responsibilities

| Controller | Purpose |
|------------|---------|
| `LoginController` | SSO login, returns cookies for subsequent requests |
| `LoginOpsController` | Login-service ops (`v1/login-ops`): active user count, ban logs, unban. Proxies XAUAT.LoginApi; requires `MAP_ADMIN_TOKEN` |
| `CourseController` | Course schedules, campus timetable (`ScheduleTime`) |
| `CalendarController` | ICS calendar subscription (`v1/course/Calendar`) |
| `ScoreController` | Exam scores |
| `ExamController` | Exam arrangements |
| `ProgramController` | Training programs / degree plans |
| `InfoController` | Academic progress |
| `PaymentController` | Campus card transactions |
| `BusController` | Shuttle bus schedules (old + new data platforms) |
| `ElectricityController` | Electricity balance, weekly usage, recharge URL, subscriptions |
| `MapController` | Campus map POI (CRUD, search by category/campus/keyword) |
| `SchoolNavController` | Campus navigation categories |
| `AppController` | App version check |

### Background Services

The app runs several `BackgroundService` instances registered in `ServiceCollectionExtensions`:

- **ScorePersistenceBackgroundService** - persists crawled scores to DB via `ChannelScorePersistenceQueue`
- **ElectricitySubscriptionMonitorBackgroundService** - periodically scans active electricity subscriptions
- **ElectricityNotificationBackgroundService** - sends low-balance email alerts via `ChannelElectricityNotificationQueue`

### Rate Limiting

A `ConcurrencyLimiter` policy named `"EduCrawler"` (permit limit 8, queue limit 16) protects the external edu system from overload. The `EduCrawlerRateLimitFilter` action filter applies it to relevant endpoints.

## Configuration

The project uses **DotNetEnv** to load configuration from a `.env` file at the solution root (auto-discovered by walking up from the working directory). There is no `appsettings.json` dependency.

`ServiceConfiguration.FromEnvironment()` reads all config from env vars. `EnvironmentVariableHelper` supports fallback names (e.g., `SQL` or `Sql__ConnectionString`).

### Key Environment Variables

| Variable | Purpose |
|----------|---------|
| `SQL` | PostgreSQL connection string; if empty, SQLite is used |
| `REDIS` | Redis connection string; if empty, Redis is skipped |
| `ELECTRICITY_SUBSCRIPTION_SCAN_INTERVAL_MINUTES` | Subscription scan interval (default 15) |
| `ELECTRICITY_SUBSCRIPTION_NOTIFICATION_COOLDOWN_MINUTES` | Notification cooldown (default 720) |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM_ADDRESS` | SMTP config for email notifications |
| `TEST_ACCOUNT_ENABLED` | Enable test account bypass (default false) |
| `PROMETHEUS_ENABLED` | Enable Prometheus metrics (default true) |
| `PAYMENT_API_BASE_URL` | XAUAT.PaymentAPI base URL; **required** — startup fails if missing |
| `LOGIN_API_BASE_URL` | XAUAT.LoginApi base URL; if empty, falls back to the Flask at `schedule.xauat.site` |
| `START`, `END` | Semester first/last day (`yyyy-MM-dd`). Read by `IInfoService.GetTime()`; `START` is also the anchor the calendar expands week numbers from, so a wrong value shifts every course event by whole weeks |

### Database

- **Development**: SQLite (`Data.db`, auto-created) with file-based data protection keys
- **Production**: PostgreSQL with NpgsqlDataProtection
- **DbContext**: `EduApi.Data/EduContext` — registered via `DbContextFactory`, so inject `IDbContextFactory<EduContext>`
- Migrations are **auto-applied at startup** (`Program.cs`)

### CORS

Allowed origins: `*.zeabur.app`, `*.xauat.site`, `http://localhost:*`. Credentials are permitted.

### Startup Flow

1. Load `.env` via DotNetEnv
2. Build `ServiceConfiguration` from env vars
3. Register all services (DB, Redis, repos, business services, HTTP clients, rate limiter, cache)
4. Auto-apply pending EF Core migrations
5. Fire-and-forget cache warmup (semesters key)
6. Start listening

## Caching

Cache key format: `eduapi:{module}:{entity}:{identifier}`. All keys are defined in `CacheKeys` (in `RedisExtensions.cs`).

- **ICacheService** (`GetOrCreateAsync`) coordinates L1 (MemoryCache) → L2 (Redis) → data source
- Redis failure auto-degrades to local cache
- Map POI data uses 24h TTL

See `CACHE_STRATEGY.md` for full details.

## Time zone

The container image sets no `TZ` and ships no tzdata, so `DateTime.Now` inside the container is **UTC**
while every domain time (timetable, exams, semester window) is Asia/Shanghai wall-clock. Use
`SchoolClock.Now` (`XAUAT.EduApi/Services/SchoolClock.cs`) instead of `DateTime.Now` for anything
that means "now" in school terms. Do not add `TZ=Asia/Shanghai` to the image: it would also move
Serilog timestamps and every other `DateTime.Now` in the process.

## Authentication Flow

1. `LoginController` receives credentials
2. `HttpLoginService` POSTs `auth/login` to the login service — `LOGIN_API_BASE_URL`
   (XAUAT.LoginApi) when set, otherwise it falls back to the Flask at
   `https://schedule.xauat.site`. The fallback is resolved in DI, so the service
   itself is unaware of the branch. Both implementations share one contract.
3. `CookieCodeService` extracts student ID from returned cookies. The login service
   deliberately does not return it (Flask never did), so this is a second upstream call.
4. Subsequent requests pass cookies via `Cookie` or `xauat` header

`HttpLoginService` maps upstream status to exceptions, and that mapping *is* the
external contract: `401` / `200+success=false` → `LoginFailedException` (controller
returns 401), `403+banned` → `AccountBannedException` (returns 429 + ban message with
a `Retry-After` derived from `ban_until`), anything else → 500.

## New POI Data Import

When adding campus map POI data:
- Source data lives in `map_data/campus_map.xlsx`
- Convert to JSON via Python: `python3 -c "import openpyxl, json; ..."`
- Import into DB via `POST /Map/import/batch` with the JSON array
- POI model schema: `EduApi.Data/Models/MapPoiModel.cs`

## Testing

- **Framework**: xUnit with Moq
- **Unit tests**: `Tests/Services/` — mock-based
- **Integration tests**: `Tests/Integration/` — in-memory EF Core
- **Performance tests**: `Tests/Performance/` — BenchmarkDotNet

## API Documentation

- OpenAPI spec: `/openapi/v1.json`
- Scalar UI: `/scalar/v1`
- Prometheus metrics: `/metrics`

## Key Dependencies

- .NET 10.0, EF Core 10.0, Npgsql, SQLite
- StackExchange.Redis, Scalar.AspNetCore, Serilog
- Flurl.Http, HtmlAgilityPack (HTML parsing), MailKit (email notifications)
- DotNetEnv, Polly (HTTP retry), Newtonsoft.Json
