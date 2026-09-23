# AGENTS.md

请使用中文回答

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

XAUAT EduApi is an ASP.NET Core Web API that provides data access interfaces for Xi'an University of Architecture and Technology's educational administration system. It scrapes and proxies data from the university's systems, providing standardized RESTful APIs for mobile apps and other clients.

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

# Run performance benchmarks (from Tests directory)
dotnet run -c Release --project Tests/XAUAT.EduApi.Tests.csproj -- --filter "*PerformanceTests*"
```

## Architecture

### Solution Structure

- **XAUAT.EduApi/** - Main ASP.NET Core Web API project
- **EduApi.Data/** - Data access layer with Entity Framework Core
- **Tests/** - xUnit tests (unit, integration, performance)

### Layered Architecture

1. **Controllers** (`XAUAT.EduApi/Controllers/`) - REST endpoints for login, courses, scores, exams, payments, etc.
2. **Services** (`XAUAT.EduApi/Services/`) - Business logic; services call external educational system via HTTP
3. **Repositories** (`XAUAT.EduApi/Repos/`) - Data access with generic repository pattern
4. **Caching** (`XAUAT.EduApi/Caching/`) - Three-tier cache: local MemoryCache → Redis → database

### Authentication Flow

The system delegates the SSO handshake to a separate login service:
1. `LoginController` receives credentials
2. `HttpLoginService` POSTs `auth/login` to XAUAT.LoginApi when `LOGIN_API_BASE_URL`
   is set, otherwise it falls back to the Flask at `https://schedule.xauat.site`.
   The fallback is resolved in DI, so the service itself is unaware of the branch.
3. `CookieCodeService` extracts student ID from returned cookies. The login service
   deliberately does not return it (Flask never did), so this is a second upstream call.
4. Subsequent requests pass cookies via `Cookie` or `xauat` header

### Database Configuration

- **Development**: SQLite (`Data.db` file)
- **Production**: PostgreSQL (connection string via `SQL` environment variable)
- **DbContext**: `EduApi.Data/EduContext.cs`

### Key Environment Variables

| Variable | Purpose |
|----------|---------|
| `SQL` | PostgreSQL connection string (production) |
| `REDIS` | Redis connection string |
| `START`, `END` | Semester first/last day (`yyyy-MM-dd`). `START` anchors the calendar's week-number expansion |
| `PAYMENT_API_BASE_URL` | XAUAT.PaymentAPI base URL; **required** — startup fails if missing |

### Time zone

The container sets no `TZ` and has no tzdata, so `DateTime.Now` is **UTC** inside it, while timetable,
exam and semester times are Asia/Shanghai wall-clock. Use `SchoolClock.Now` for "now" in school terms.

### Caching Strategy

Multi-level caching with TTL + LRU/LFU hybrid eviction:
- L1: Local MemoryCache (sub-millisecond)
- L2: Redis distributed cache
- Automatic fallback when Redis unavailable
- Cache key format: `{Prefix}:{BusinessModule}:{EntityType}:{Id}`

See `CACHE_STRATEGY.md` for detailed caching documentation.

## Testing

- **Framework**: xUnit with Moq for mocking
- **Unit tests**: `Tests/Services/` - mock-based service tests
- **Integration tests**: `Tests/Integration/` - use in-memory EF Core database
- **Performance tests**: `Tests/Performance/` - BenchmarkDotNet benchmarks

## API Documentation

- OpenAPI spec: `/openapi/v1.json`
- Scalar UI: `/scalar/v1`
- Prometheus metrics: `/metrics`
