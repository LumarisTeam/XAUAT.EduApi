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
3. **Login** (`XAUAT.EduApi/Login/`) - Self-contained SSO subsystem; see "Login Subsystem" below
4. **Repositories** (`XAUAT.EduApi/Repos/`) - Data access with generic repository pattern
5. **Caching** (`XAUAT.EduApi/Caching/`) - Three-tier cache: local MemoryCache → Redis → database

### Authentication Flow

EduApi performs the CAS/UAAP handshake itself — there is no separate login service:
1. `LoginController` receives credentials
2. `SsoLoginService` (`Login/SsoLoginService.cs`) short-circuits the test account, otherwise
   delegates to `SsoAuthService`, which applies a three-tier priority:
   **cached eduCookie → exchange a cached SSO ticket → full CAS login**. Rate-limit budget is
   pre-acquired only on the full-login path, so a cache hit never burns it.
3. `XauatSsoClient` (`Login/Xauat/`) talks to `http://authserver.xauat.edu.cn` (plain HTTP —
   upstream is like that) and then `https://swjw.xauat.edu.cn`. The CAS POST **must not follow
   redirects**, so a full login only yields the SSO ticket (`CASTGC`); the student session is
   planted on the *second* hop of a manual redirect walk.
4. `CookieCodeService` extracts the student ID from the returned cookies — the SSO handshake
   does not produce it, so this is a second upstream call.
5. Subsequent requests pass cookies via `Cookie` or `xauat` header

Failures map as: failure / rate-limited / no cookie → `LoginFailedException` (401);
banned → `AccountBannedException` (429 + `Retry-After` from `unban_at`).

### Login Subsystem (`XAUAT.EduApi/Login/`)

Ported wholesale from the now-deprecated `XAUAT.LoginApi`. Two constraints here are non-obvious
and fail **silently** if broken:

- **The Redis keys must carry no prefix.** `LoginCacheKeys` deliberately matches the keys the
  legacy Flask service writes (`login-cookies-{user}-{sha256(pw)}`, `sso-cookies-…`,
  `security:ban:{school}:{user}`, `security:ban_logs`, `current_semester`) so caches stay warm
  and bans carry across the cutover. This is the *opposite* of `CacheKeys` (`eduapi`) and
  `CacheService` (`EduApi:`), which is why the subsystem uses a raw `IDatabase` wrapper
  (`LoginRedisStore`) instead of `ICacheService`. Prefixing the keys raises no error — it just
  makes every login slower and the active-user count read 0. Guarded by
  `Tests/Login/Compatibility/LoginCacheKeysTests.cs`.
- **Redis values are snake_case and raw UTF-8** (`RedisJson.Options`), matching Flask's
  `json.dumps(..., ensure_ascii=False)`. Both settings are storage contract, not style.

Also load-bearing: `XauatAuthClient` is registered with `AllowAutoRedirect = false` and
`UseCookies = false`. Turning either on makes *every* login fail with a plausible-looking
upstream error. Guarded by `Tests/Extensions/ServiceRegistrationTests.cs`.

### Unified Authentication (`XAUAT.EduApi/Login/Unified/`)

A **generic** CAS / 金智 UAAP login: the caller passes only a protected target URL plus
credentials (`POST /v1/login/unified`), and the server discovers the auth server from the
redirect chain (the login page URL carries the `service` parameter), parses the page's hidden
inputs + `pwdEncryptSalt`, submits the form, then walks the redirects back to plant the session
cookie. Unlike `Login/`, it uses **no Redis** — every call really logs in, so cookies rotate.

Two things here are security-relevant and easy to weaken by accident:

- **The host allow list** (`HostAllowList`, default `*.edu.cn`, extended — never replaced — by
  `UNIFIED_AUTH_ALLOWED_HOSTS`). The endpoint takes an arbitrary URL, so without it the service
  is an open SSRF proxy.
- **`UnifiedConnectGuard` on `SocketsHttpHandler.ConnectCallback`.** The allow list only checks
  the *name*; a legitimate `.edu.cn` host can still resolve to `127.0.0.1` or `169.254.169.254`
  (DNS rebinding, poisoned internal DNS). Resolving and validating at connect time means the
  check and the connection use the *same* IP. `UseProxy = false` is part of the same defense:
  with a proxy the connection is made by the proxy and the callback never runs. Guarded by
  `Tests/Extensions/ServiceRegistrationTests.cs` and `Tests/Login/Unified/`.

**`ProtocolFallbackHandler` is the deliberate second transport, and it must stay a fallback.**
`SocketsHttpHandler` validates response header names against RFC 9110 tokens and throws
`HttpRequestError.InvalidResponse` before any handler sees the response — there is no setting that
relaxes it. Real counter-example: `gmis.xauat.edu.cn` sends `SELINUX=Server:  ` on *every*
response (name contains `=`, so browsers silently drop it and the school never notices), which
made `/v1/login/unified` report "网络连接错误" for a perfectly reachable host. The fallback fires
**only** on `InvalidResponse` / `ResponseEnded`; connection, TLS and timeout errors must not
retry, or every real outage logs two different exceptions. `TolerantHttpMessageHandler` reuses
`UnifiedConnectGuard.ConnectAsync(host, port, ct)` and the system certificate chain — it must never
grow a "trust any certificate" switch or its own DNS check. Do not promote it to the primary
transport.

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
| `UNIFIED_AUTH_ALLOWED_HOSTS` | Extra target hosts for `POST /v1/login/unified` (comma-separated, `*.domain` or `*`). Built-in `*.edu.cn` is always kept |

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

> The `Login/` subsystem deliberately **bypasses** this layer (no prefix, raw values) so its keys
> stay interchangeable with the legacy Flask deployment. See "Login Subsystem" above.

## Testing

- **Framework**: xUnit with Moq for mocking
- **Unit tests**: `Tests/Services/` - mock-based service tests
- **Integration tests**: `Tests/Integration/` - use in-memory EF Core database
- **Performance tests**: `Tests/Performance/` - BenchmarkDotNet benchmarks

## API Documentation

- OpenAPI spec: `/openapi/v1.json`
- Scalar UI: `/scalar/v1`
- Prometheus metrics: `/metrics`
