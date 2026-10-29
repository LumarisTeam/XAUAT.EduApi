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
3. **Login** (`XAUAT.EduApi/Login/`) - Self-contained SSO subsystem; see "Login subsystem" below
4. **Repositories** (`XAUAT.EduApi/Repos/`) - Data access with generic repository pattern
5. **Caching** (`XAUAT.EduApi/Caching/`) - Three-tier: local MemoryCache → Redis → database

### Controllers and Their Responsibilities

| Controller | Purpose |
|------------|---------|
| `LoginController` | SSO login. Does the CAS/UAAP handshake itself, returns cookies for subsequent requests. `x-loginMode: 1` → graduate login (see below) |
| `UnifiedAuthController` | Generic unified auth (`v1/login/unified`). One target URL + credentials → discovers the CAS login page, logs in, returns the cookies. Host allow list + private-address guard; no Redis |
| `LoginOpsController` | Login ops (`v1/login-ops`): active user count, ban logs, ban status, unban. Reads the shared Redis directly; requires `MAP_ADMIN_TOKEN` |
| `CourseController` | Course schedules, campus timetable (`ScheduleTime`). `x-loginMode: 1` → graduate timetable (see below) |
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
| `UNIFIED_AUTH_ALLOWED_HOSTS` | Extra target hosts for `POST /v1/login/unified` (comma-separated, `*.domain` or `*`). Built-in `*.edu.cn` is always kept, so this only ever **adds** |
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
2. `SsoLoginService` (`Login/SsoLoginService.cs`) short-circuits the test account, otherwise
   delegates to `SsoAuthService`, which applies a three-tier priority:
   **cached eduCookie → exchange a cached SSO ticket → full CAS login** (rate-limit budget is
   pre-acquired only on the full-login path, so a cache hit never burns it).
3. `XauatSsoClient` (`Login/Xauat/`) talks to `http://authserver.xauat.edu.cn` (plain HTTP,
   upstream is like that) and then to `https://swjw.xauat.edu.cn`. **The CAS POST must not
   follow redirects**, so a full login only yields the SSO ticket (`CASTGC`); the student
   session is planted on the *second* hop of a manual redirect walk.
4. `CookieCodeService` extracts the student ID from the returned cookies — the SSO handshake
   does not produce it, so this is a second upstream call.
5. Subsequent requests pass cookies via `Cookie` or `xauat` header

`SsoLoginService` maps the login outcome to exceptions, and that mapping *is* the external
contract: failure / rate-limited / no cookie → `LoginFailedException` (controller returns 401);
banned → `AccountBannedException` (429 + `Retry-After` derived from `unban_at`); anything
else → 500.

### Login subsystem (`XAUAT.EduApi/Login/`)

Ported wholesale from the now-deprecated `XAUAT.LoginApi`. Two constraints here are
non-obvious and fail **silently** if broken:

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

### Unified authentication subsystem (`XAUAT.EduApi/Login/Unified/`)

A **generic** CAS / 金智 UAAP login behind `POST /v1/login/unified`: the caller supplies only a
protected target URL + credentials. The server follows the redirect chain to find the auth
server (the login page URL carries `service=`, and that URL is what it later gets bounced back
to), transparently forwards the page's `type="hidden"` inputs, encrypts the password with
`pwdEncryptSalt` when present, POSTs **without following redirects** (the ticket only lives on
that one 302), then walks the redirects back capturing `Set-Cookie` hop by hop. It reuses
`XauatCookieJar` / `XauatHtmlParser` / `PasswordEncryptor` but shares no state with the XAUAT
link — changing it cannot break `POST /v1/login`.

Deliberately **no Redis**: every call really logs in, so cookies rotate; the
`login-cookies-*` compatibility keys stay XAUAT-only.

Two guards make an arbitrary-URL endpoint safe, and both fail silently if weakened:

- `HostAllowList` — default `*.edu.cn`, extended (never replaced) by `UNIFIED_AUTH_ALLOWED_HOSTS`.
- `UnifiedConnectGuard` on `ConnectCallback` — resolves DNS and drops private/reserved IPs *at
  connect time*, so the check and the connection use the same address (a request-time check is
  TOCTOU and loses to DNS rebinding). `UseProxy = false` belongs to the same defense: through a
  proxy the callback never runs. Guarded by `Tests/Extensions/ServiceRegistrationTests.cs` and
  `Tests/Login/Unified/`.

**`ProtocolFallbackHandler` is the deliberate second transport, and it must stay a fallback.**
`SocketsHttpHandler` validates response header names against RFC 9110 tokens and throws
`HttpRequestError.InvalidResponse` before any handler sees the response — no setting relaxes it.
Real counter-example: `gmis.xauat.edu.cn` sends `SELINUX=Server:  ` on *every* response (name
contains `=`, so browsers silently drop it and the school never notices), which made
`/v1/login/unified` report "网络连接错误" for a perfectly reachable host. The fallback fires
**only** on `InvalidResponse` / `ResponseEnded`; connection, TLS and timeout errors must not
retry, or every real outage logs two different exceptions. `TolerantHttpMessageHandler` reuses
`UnifiedConnectGuard.ConnectAsync(host, port, ct)` and the system certificate chain — it must never
grow a "trust any certificate" switch or its own DNS check. Do not promote it to the primary
transport.

### 研究生教务系统（`x-loginMode: 1`）

研究生系统（`gmis.xauat.edu.cn`，参考实现见仓库外的 `py_src/`）只覆盖**登录**与**课表**；
校车、电费、饭卡、地图等非教务接口对两个系统是通用的，不读这个头。

通用头契约（`LoginModeExtensions`）：

| `x-loginMode` | 含义 |
|---|---|
| 缺失 / 空 / `0` | 本科生系统（默认，`swjw.xauat.edu.cn`） |
| `1` | 研究生系统（`gmis.xauat.edu.cn`） |
| 其它 | 400 `10001`（`ApiMessageKey.InvalidLoginMode`），**不**静默回落到本科生 |

只有 `POST /v1/login`（旧路由 `/Login`）与 `GET /v1/course`（旧路由 `/Course`）读取它；
OpenAPI 文档由 `LoginModeHeaderOperationTransformer` 只给这两个控制器补参数
（`/v1/course/ScheduleTime` 是通用作息表，刻意排除）。

- **登录**：`GraduateLoginService` 把目标地址固定为
  `https://gmis.xauat.edu.cn/pyxx/sso/login`，直接复用 `IUnifiedAuthService`
  （`Login/Unified/`）完成发现/CAS 登录/回跳。与本科生链路的三处刻意差异：
  **不接 Redis**（通用认证每次都会真登录、cookie 会轮换，`login-cookies-*` 是本科生兼容键）、
  **不做封禁/登录限流**、**学号直接取用户名**（研究生登录页不返回学号）。
  凭据错 → `LoginFailedException`（401）；目标地址被通用认证层拒绝或上游不可达 →
  `HttpRequestException`（502）。
- **课表**：`GraduateCourseService` 带 cookie GET
  `https://gmis.xauat.edu.cn/pyxx/pygl/xskbcx`，由 `GraduateTimetableParser`
  解析成与本科同构的 `List<CourseActivity>`（页面是
  `var td = document.getElementById("td_星期_节次"); td.innerHTML += "…"` 的 JS 片段，
  不是 `<table>`）。缓存键必须是 `CacheKeys.GraduateCourses(studentId)`，**不能**与
  `CacheKeys.Courses` 共用——同一学号在两个系统是两份课表，共用键不会报错，只会串数据。
- **Cookie 提取**：用 `Request.GetGraduateAuthCookie()`，不是 `GetEduAuthCookie()`。
  研究生 cookie 没有 `__pstsid__` 特征，后者会把它当成非教务 cookie 丢掉。
- **传输层**：必须复用命名客户端 `UnifiedSsoClient.ClientName`（`UnifiedAuthClient`）。
  `gmis` 每条响应都带非法头名 `SELINUX=Server`，普通 `SocketsHttpHandler` 在解析响应头时
  就会抛 `InvalidResponse`；该客户端已挂 `ProtocolFallbackHandler`，且关掉了自动重定向
  （cookie 失效时的 302 就是我们的 401 判定依据）。
- **字符集**：`gmis` 的 `Content-Type` 不带 charset，`GraduateHtmlDecoder` 按
  Content-Type → `<meta charset>` → UTF-8 合法性 → GB18030 的顺序解码。若按 UTF-8
  硬解 GBK 页面，中文会静默乱码、正则匹配不到课程，表现为"登录成功但课表为空"。

`py_src/` 只是移植参考，不是运行时依赖；改动解析规则时请同步核对
`python3 -c "..."` 的原始输出。

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
