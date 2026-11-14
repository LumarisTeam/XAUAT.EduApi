# XAUAT.EduApi MFA / re-auth 实现计划

## Context

生产 `POST /v1/login` 的 401 长期被误报为「密码错误」。实测确认：

1. **直接原因是密码过期（90 天策略）**：authserver 接受密码后把登录重定向到 `/authserver/improveInfo/improveUserInfo.do`，`updateUserInfo.do` 返回「当前密码已过期…」。现有 `XauatSsoClient` 把这个 200/302 页面当成 SSO 票据或凭据错误，最终统一变成 401。
2. **第二层是 re-auth（二次认证）**：财务综合服务平台、企微连接器会 302 到 `/authserver/reAuthCheck/reAuthLoginView.do`，现有代码完全不认识。
3. 学校没有全局登录 MFA；`swjw`/`gmis` 不触发 re-auth，财务/企微才触发；异地登录（`ipAnomaly>0`）会强制 `reAuthType=2`。

用户已确认的方案：**两步 + 轮询**；**扫码纳入第一版**；**OTP 只支持交互式 `otpCode`（不做 otpSecret/TOTP）**；**TrustDevice 默认 true**；**企业微信验证码（type 4）分支要实现**（当前 app 策略拒绝，等学校启用后可用）。

## 已实测的 re-auth 协议

触发：`302 /authserver/reAuthCheck/reAuthLoginView.do?service=<app>`；页面内联 `var reAuthParams = {...}`（`service`、`reAuthType`、`pwdEncryptSalt`、`isMultifactor`、`isSleepAccount`、`ipAnomaly`、`reAuthUserId`、`otpCodeRequired`、`_isBindOtp`）。

提交：`POST /authserver/reAuthCheck/reAuthSubmit.do`（form-urlencoded）：

| reAuthType | 方式 | 发送/提交 |
|---:|---|---|
| 2 | 密码 | `password=AES(password, reAuthParams.pwdEncryptSalt)`；首次请求内自动完成 |
| 10 | 安全令牌 OTP | `otpCode=<6 位>`；**已实测通过** |
| 3 | 短信 | 发送 `getDynamicCodeByReauth.do` + `authCodeTypeName=reAuthDynamicCodeType`；提交 `dynamicCode`；**已实测通过** |
| 4 | 企业微信验证码 | 发送 `authCodeTypeName=reAuthWChatDynamicCodeType`（返回 `wechat_success`）；提交 `dynamicCode`；**当前 app 策略拒绝，协议已实现** |
| 5 / 11 / 12 / 13 | 今日校园/邮箱/钉钉/WeLink | 发送 `reAuthCpdailyDynamicCodeType` / `reAuthEmailDynamicCodeType` / `reAuthDingTalkDynamicCodeType` / `reAuthWeLinkDynamicCodeType`；提交 `dynamicCode` |
| 8 | 微信扫码 | `combinedLogin.do?type=weixin&reAuth=1&success=<service>` → `open.weixin.qq.com/connect/qrconnect` → 二维码 UUID → 轮询 `lp.open.weixin.qq.com/connect/l/qrconnect?uuid=...` → `wx_errcode=405` + `wx_code` → `callback?code=&state=` → `login?service=` → `ticket=ST-...`；**已实测通过** |
| 9 / 14 | QQ / 钉钉·企微扫码 | 同一 QR 架构，第一版实现 provider 注册但标记未实测 |

固定字段：`service`、`reAuthType`、`isMultifactor`、`uuid=""`、`answer1=""`、`answer2=""`、`skipTmpReAuth=<TrustDevice>`。成功：`{"msg":"认证成功","code":"reAuth_success"}`；失败：`reAuth_failed` / `reAuth_unauthorized`。成功后 `/authserver/login?service=...` → 目标站点 `?ticket=ST-...`。

## API 契约

### 首次请求遇到 MFA

`/v1/login` 与 `/v1/login/unified` 返回 **HTTP 200 + `ApiResponse.Code=20003 (MfaRequired)`**，`data` 仍是 `LoginResponse` / `UnifiedAuthResponse`，新增：

```csharp
public bool MfaRequired { get; set; }
public MfaChallenge? Mfa { get; set; }
public bool PasswordExpired { get; set; }
public string? PasswordChangeUrl { get; set; }
```

`MfaChallenge`：`SessionId`、`Method`、`ReAuthType`、`MaskedTarget`、`QrCodeUrl`、`QrImageBase64`、`ExpiresAt`、`Message`、`Hint`、`IsQr`。

首次请求 DTO 增加可选字段：`MfaMethod`（`auto`/`password`/`otp`/`sms`/`wecom`/`today_campus`/`email`/`dingtalk`/`welink`/`wechat_qr`/`qq_qr`/`dingtalk_qr`）与 `TrustDevice`（默认 `true`）。`auto` 用页面默认类型；请求了不支持的类型（如当前 app 的 `wecom`）→ `400 + 20007 MfaMethodUnsupported`，不静默回落。

### 第二步

- `POST /v1/login/mfa`：`{ mfaSessionId, code?, trustDevice? }`；成功返回 `LoginResponse` 或 `UnifiedAuthResponse`（按会话 kind）。
- `GET /v1/login/mfa/status?mfaSessionId=...`：扫码轮询；`pending`/`scanned` 返回 202 + `Retry-After`；`success` 返回最终登录结果；`expired` 返回 410。

### 错误码 / HTTP

| 场景 | HTTP | Code |
|---|---:|---:|
| MFA 挑战 | 200 | 20003 |
| 密码过期 | **428** | 20004 |
| MFA 会话过期/不存在 | 410 | 20005 |
| 验证码错误/上游拒绝 | 401 | 20006 |
| MFA 方式不支持 | 400 | 20007 |
| MFA 不可用（Redis 不可用） | 503 | 50004 |
| 限流/尝试耗尽/并发忙 | 429 | 40001 |

`MfaRequiredException`、`PasswordExpiredException`、`MfaUnavailableException` 必须在 `LoginFailedException` **之前**捕获。密码过期不继承 `LoginFailedException`，避免被旧 catch 吞掉。

## 核心组件

### 新增 `XAUAT.EduApi/Login/Mfa/`

- `MfaMethod.cs`：枚举 + `TryParse` + reAuthType/`authCodeTypeName` 映射。
- `MfaSeed.cs`：内部结果载荷（kind、username、authServerLoginUrl、service、reAuthType、method、encryptSalt、isMultifactor、isSleepAccount、maskedTarget、hint、cookies、entryUrl、sessionCacheKey/ticketCacheKey、passwordHash、qr 信息）。
- `MfaPendingSession.cs`：Redis 存储模型，字段同上 + `QrStatus`、`Attempts`、`MaxAttempts=5`、`CreatedAt/ExpiresAt`。**绝不存明文密码/OTP/短信码/企业微信码/二维码 code**；`PasswordHash` 是 SHA-256，仅用于复刻既有缓存键。
- `MfaCacheKeys.cs`：`security:mfa:{id}`、`security:mfa:lock:{id}`、`security:mfa:poll:{id}`；不要放进 `LoginCacheKeys`（后者是 Flask 兼容契约）。
- `IMfaSessionStore` / `MfaSessionStore`：Singleton；`Create/Get/Update/Delete`、`TryAcquireLock`（SET NX，15s）、`TryAcquirePoll`（SET NX，1s）；TTL 5 分钟；使用 `RedisJson.Options`（snake_case）；过期/成功/失败后删除。
- `ReAuthPageParser.cs`：正则逐字段解析 `reAuthParams`（页面是 JS 对象，可能有 `null`/单引号/尾逗号）；解析失败 → 协议异常 502，不能退化成密码错误。
- `ReAuthHandler.cs`：`StartAsync` / `CompleteAsync` / `PollQrAsync`。
  - `StartAsync`：type 2 → seed（首次请求内自动完成）；type 10 → seed + hint（不发送）；type 3/4/5/11/12/13 → `getDynamicCodeByReauth.do` 发送并返回 seed；type 8/9/14 → 启动 combinedLogin、取二维码。
  - 切换方式：请求的 `MfaMethod` 与页面 `reAuthType` 不同 → `changeReAuthType.do`；只有响应 `code=="1"` 才继续，`code=="0"` → `MfaMethodUnsupported`。
  - `CompleteAsync`：`reAuthSubmit.do`，`password`/`otpCode`/`dynamicCode` 按类型填；成功后 `login?service=...` 跟票；失败映射 `InvalidCode`/`Unauthorized`/`Upstream`。
  - `PollQrAsync`：HTTP 408/404/405/403 分支；405 后 `callback?code&state` → `login?service` → ticket。
- `QrProviderRegistry.cs`：type 8 = weixin（已实测）；9 = qq、14 = dingtalk（架构就绪，未实测）；未知 302 host 安全失败，不跟随任意 host。
- `MfaQrClient.cs`：独立命名客户端，只访问硬编码微信域名（`open.weixin.qq.com`/`lp.open.weixin.qq.com`）；`AllowAutoRedirect=false`、`UseCookies=false`、`UseProxy=false`、`ConnectCallback=UnifiedConnectGuard.ConnectAsync`、长轮询超时 35s；不把 authserver cookie 发给微信。
- `MfaLoginService.cs`：第二步编排（Scoped）；加载 `MfaPendingSession`，恢复 jar，调 `ReAuthHandler.CompleteAsync/PollQrAsync`，按 kind 返回 `LoginResponse`/`UnifiedAuthResponse`；成功后写既有缓存。

### 既有组件小改

- `XauatCookieJar.Restore(IEnumerable<(Host,Name,Value)>)`：从 pending session 恢复多 host cookie。
- `XauatHttpSession.SendFollowingRedirectsAsync(..., CancellationToken ct, Func<Uri,HttpResponseMessage,bool>? stopBeforeFollow = null)`：在跟到 re-auth/improveInfo 前停止，保留中间响应。
- `ILoginRedisStore` 增加 `SetIfNotExistsAsync(key,value,ttl)`（必需，用于锁/轮询节流）；`GetDeleteAsync`（可选，GETDEL，用于严格一次性消费）。同步 `LoginRedisStore` 与 `FakeLoginRedisStore`。
- `XauatConstants` 增加 re-auth/improveInfo/QR 端点常量；`combinedLogin.do`/callback 使用**当前 authserver origin**（通常是 `https://authserver.xauat.edu.cn`），不要硬编码 HTTP。
- `LoginToken` 增加 `LoginFailureKind Failure` + `MfaSeed? Mfa`；`SsoAuthResult` 增加 `SsoAuthFailureKind` + `MfaChallenge? Mfa`；`UnifiedAuthOutcome` 增加 `MfaSeed? Mfa`；`UnifiedAuthFailure` 末尾增加 `MfaRequired`、`PasswordExpired`、`MfaUnavailable`，**switch 必须显式映射**（默认分支会把新枚举报成 502）。
- `UnifiedAuthCommand` 增加可选 `MfaMethod`/`TrustDevice`（保留三参数构造）。

## 三条链路接入

### 标准 `/v1/login`

- `XauatSsoClient.InterpretLoginResponseAsync`：
  - 302 `Location` 是 `reAuthLoginView.do` → 停、GET 页面、解析、返回 `LoginToken.ReAuth(seed)`；是 `improveUserInfo.do` → `PasswordExpired`。
  - 200 正文先判 re-auth、再判密码过期，最后才是 `退出/logout`/`span#msg`。
- `ExchangeSsoTicketAsync`：用 `stopBeforeFollow` 识别 re-auth/improveInfo；命中 re-auth 返回 MFA，**不要删除缓存票据**；命中 improveInfo 返回密码过期。
- `SsoAuthService`：
  - 缓存 CASTGC 换票路径遇到 type 2 → 自动 `CompleteReAuthAsync(seed, null, password, trustDevice)`；成功写缓存；失败返回凭据失败。交互式 → 创建 pending session，返回 `MfaRequired`（不写 edu/sso 缓存）。
  - 完整登录路径已预占 `rateLimitKey`；遇到交互式 MFA 先 **release 预占名额**（密码已验证正确），再创建会话；type 2 auto 成功按正常成功路径 release/write；密码过期 release。
  - **MFA 第二步不调用 `CasAttemptGuard.AcquireAsync`**，同一次登录只占一个名额。
- `SsoLoginService`：按 `SsoAuthFailureKind` 抛 `MfaRequiredException` / `PasswordExpiredException` / `MfaUnavailableException` / `LoginFailedException`；新增可选 `MfaRequestOptions` 重载，旧方法委托。

### 通用 `/v1/login/unified` 与研究生

- `UnifiedSsoClient.SubmitCredentialsAsync` / `FollowBackAsync`：
  - 302 `Location` 是 re-auth/improveInfo → 停并返回对应 outcome；
  - 200 正文先判 re-auth/密码过期；
  - 缓存 CASTGC 换票被 re-auth 拦截时返回 MFA，且**不删除** ticket key。
- 新增 `CompleteReAuthAsync` / `PollReAuthQrAsync`，成功后用 seed 里的 `SessionCacheKey`/`TicketCacheKey` 写既有 `unified:session:*`（2 分钟）/`unified:ticket:*`（1 天）。
- `UnifiedAuthService` switch 显式处理 `MfaRequired`（type 2 自动完成；交互式创建会话并抛 `MfaRequiredException`）、`PasswordExpired`、`MfaUnavailable`；**禁止落到 `_ => HttpRequestException`**。
- `GraduateLoginService` 透传 `MfaMethod`/`TrustDevice`，`MfaRequiredException`/`PasswordExpiredException` 原样上抛，控制器统一处理。
- 缓存隔离：MFA 状态只进 `security:mfa:*`；`login-cookies-*`/`sso-cookies-*`/`unified:session:*`/`unified:ticket:*` 只在 MFA 成功且拿到最终 cookie 后写入；失败不删除旧缓存。

## 密码过期与持久化登录

### 识别与返回

- 识别 URL `/improveInfo/improveUserInfo.do` 或正文「当前密码已过期」；标准/统一链路的所有 302/200 判定点都要加，且**先于 re-auth 判断**（密码过期是更前置的阻塞）。
- 抛 `PasswordExpiredException`；`/v1` 返回 **428 + 20004**，`data.passwordExpired=true`，`data.passwordChangeUrl=https://authserver.xauat.edu.cn/authserver/improveInfo/improveUserInfo.do`（页面会要求先登录；客户端引导用户在浏览器打开）。文案「当前密码已过期，请先修改统一身份认证密码」（八语言）。
- 旧 `/Login` 返回 428 + `LoginResponse{Success=false, PasswordExpired=true, PasswordChangeUrl=...}`；不要继续返回 401「密码错误」。
- **API 不自动改密**：改密需要旧密码 + 新密码 + 确认密码，且是敏感操作；由用户在浏览器完成，客户端更新自己保存的密码后重试。

### 对持久化登录的影响

现有缓存优先级（标准链路）：

1. `login-cookies-{user}-{sha256(password)}`（eduCookie，20 分钟）
2. `sso-cookies-{user}-{sha256(password)}`（CASTGC，7 天）
3. 完整登录

关键点：

- **缓存命中时 API 不会访问 authserver，因此不知道密码已过期**。已登录用户可能继续使用旧 session 直到 TTL（eduCookie 20 分钟，SSO 票据 7 天）。这是学校“已登录会话”的既有行为，不建议为每次请求增加上游校验（会绕开缓存并消耗 `CasAttemptGuard`）。
- **一旦检测到密码过期**（完整登录或 SSO 票据换票被 authserver 拦到 `improveInfo`）：
  - 写 `security:password_expired:{username}:{sha256(password)}`，TTL 24 小时（可配置），值 `1`。
  - 删除该账号/该密码哈希下的 `login-cookies-*`、`sso-cookies-*`（标准链路）以及 `unified:session:*`、`unified:ticket:*`（通用链路）。
  - 释放 `BanService` 预占的失败名额（密码本身正确，不应计入失败）/ 返回 428 + 20004。
  - 之后使用**同一旧密码**的缓存查询先检查该 flag；命中则直接返回 `PasswordExpired`，不再返回旧 cookie/票据，避免“检测到过期后又被缓存救活”。
- **用户改密后**：
  - 新密码的 SHA-256 不同，所有缓存键（`login-cookies-*`、`sso-cookies-*`、`unified:session:*`、`unified:ticket:*`、`security:password_expired:*`）自然切换到新命名空间；旧键不可达，按 TTL 过期，不需要迁移。
  - 新密码第一次登录是完整登录（密码未过期时）；成功后按原逻辑写入 eduCookie（20 分钟）、SSO 票据（7 天）、unified 缓存（2 分钟/1 天），持久化登录恢复。
  - 客户端必须在用户改密后更新自己保存的凭据；否则旧密码继续得到 428/20004（或学校拒绝旧密码时的 401）。API 不保存明文密码。
- **通用链路**：`unified:session:*`（2 分钟）和 `unified:ticket:*`（1 天）同样按密码哈希隔离；`PasswordExpired` 时删除这两个键并设置 flag；`ReadSessionAsync`/`LoginCoreAsync` 命中缓存前检查 flag。
- **缓存键与 flag 的写入点**：
  - 标准链路：`SsoAuthService` 已有 `passwordHash`/`ILoginRedisStore`/`BanService`；在 `eduCookie`/`ssoCookie` 读取前检查 flag，检测到过期时写 flag、清缓存、release 名额。
  - 通用链路：`UnifiedSsoClient` 有 `redis` 和 `CacheKey(...)`；在 `ReadSessionAsync`/票据复用前检查 flag，检测到过期时删除 `targetKey`/`ticketKey` 并写 flag。
  - CPU/Redis 成本：每次登录缓存查询多一次 `GET security:password_expired:*`；只有已标记过期的账号才命中，正常账号为一次空 GET，可接受。
- **延迟检测的边界**：如果用户一直命中缓存且从不触发完整登录/换票，API 不会主动知道密码过期；这是缓存换来的性能取舍。若需要更严格，可在缓存命中时增加一次轻量 authserver 校验，但不建议默认开启。

## 控制器与路由

- `LoginController` / `UnifiedAuthController`：在 `LoginFailedException` 前加 `catch (MfaRequiredException)`（200 + 20003 + data.Mfa）、`catch (PasswordExpiredException)`（428 + 20004）、`catch (MfaUnavailableException)`（503 + 50004）。
- 新增 `Controllers/V1/MfaController.cs`：`POST /v1/login/mfa`、`GET /v1/login/mfa/status`；用 `MfaLoginService`；成功返回 `ApiResponse<object>` 承载 `LoginResponse`/`UnifiedAuthResponse`。
- 旧 `Controllers/Old/LoginController.cs`：type 2 透明成功；交互式 MFA 返回 200 + `LoginResponse{Success=false,MfaRequired=true,Mfa=...}`（保住 sessionId）；密码过期 428；MFA 不可用 503。
- `ApiCodes`：新增 20003–20007、50004；`ApiMessageKey`/`ApiMessageLocalizer` 八语言补齐。

## 实施顺序

0. **生产前置**：把生产/客户端密码更新为已验证的新密码；等 `CasAttemptGuard` 的 `Retry-After` 重置，不要循环重试。
1. **DTO/错误码/本地化/异常**：改 4 个 DTO、`ApiCodes`、`ApiMessageKey`、`ApiMessageLocalizer`，新增 5 个异常；跑 `dotnet build` + 现有测试。
2. **Redis 与会话**：扩展 `ILoginRedisStore`/`LoginRedisStore`/`FakeLoginRedisStore`；实现 `MfaSessionStore` + 测试；新增 `LoginCacheKeys.PasswordExpired(username, passwordHash)`（`security:password_expired:*`）。
3. **解析器与 Handler**：`ReAuthPageParser` → `ReAuthHandler`（先 2/10/3/4/5/11/12/13，再 8/9/14）；用 `ScriptedHttpHandler` 独立测试。
4. **标准链路**：`XauatCookieJar.Restore`、`XauatHttpSession` 停止谓词、`LoginToken`、`XauatSsoClient`、`SsoAuthService`、`SsoLoginService`；type 2 自动完成优先落地；`SsoAuthService` 在缓存查询前检查密码过期 flag，检测到过期时写 flag、清缓存、release 名额。
5. **通用/研究生链路**：`UnifiedSsoClient`、`UnifiedAuthService`、`GraduateLoginService`；显式映射新枚举；`UnifiedSsoClient` 在 `ReadSessionAsync`/票据复用前检查 flag，检测到过期时删除 `targetKey`/`ticketKey` 并写 flag。
6. **控制器/DI**：两个登录控制器、`MfaController`、旧控制器；注册 `MfaSessionStore`/`ReAuthHandler`/`MfaLoginService`/`MfaQrClient`；跑 `ServiceRegistrationTests`。
7. **全量回归**：`dotnet test`，重点登录/控制器/本地化/兼容键/注册。
8. **生产验证矩阵**：见下。

## 测试计划（关键用例）

- `ReAuthPageParserTests`：type 2/10、单/双引号、尾逗号、缺字段、密码过期页识别。
- `ReAuthHandlerTests`：
  - type 2 用 **re-auth 页的盐**加密密码；`skipTmpReAuth=true`；
  - type 10 `otpCode=123456`；type 3 `dynamicCode`；type 4 发送 `reAuthWChatDynamicCodeType`、提交失败返回 `InvalidCode`；
  - `changeReAuthType` code 0 → `MfaMethodUnsupported`；
  - `reAuth_failed`/`reAuth_unauthorized`；成功跟票拿 eduCookie/CASTGC；
  - QR：`combinedLogin.do?type=weixin&reAuth=1&success=...`、302 host 校验、UUID 解析、二维码下载；poll 408/404/405/403；405 后 callback + ticket；state 不匹配失败；
  - 不重复消耗 `CasAttemptGuard`。
- `MfaSessionStoreTests`：key/TTL、过期删除、不延长绝对过期、SET NX 锁、轮询节流、序列化不含明文、Redis 不可用。
- `PasswordExpiredTests`：完整登录/换票返回 improveInfo → 428/20004；写 `security:password_expired:{username}:{sha256(password)}`；删除 `login-cookies-*`/`sso-cookies-*`/`unified:session:*`/`unified:ticket:*`；释放限流名额；同一旧密码的缓存查询被 flag 拦截；改密后新密码哈希写入新缓存并成功；无 flag 的旧 eduCookie 仍按缓存返回（记录该限制）。
- `MfaControllerTests`：首次 20003 + challenge（standard/unified）、第二步成功、401/410/429/503/400、QR pending/scanned/success/expired、type 4 20007、TrustDevice 透传、密码过期 428。
- 扩展 `UnifiedSsoClientTests`/`UnifiedAuthServiceTests`：re-auth 302/200、缓存票据不被删除、密码过期、type 2 完成后写 unified 缓存、QR 脚本。
- 扩展 `SsoAuthServiceTests`/`SsoLoginServiceTests`：MFA 不写缓存、交互式 release 限流名额、type 2 自动成功、密码过期、Redis 不可用。
- 扩展 `GraduateLoginServiceTests`、`LoginModeControllerTests`、`ServiceRegistrationTests`、`ApiMessageLocalizerTests`。
- `FakeResponses` 增加 JSON/Binary；`FakeLoginRedisStore` 增加 `SetIfNotExistsAsync`/`GetDeleteAsync`；`FakeSsoClient` 增加 `CompleteReAuthResult`/`PollQrResult`。

## 生产验证矩阵

1. `POST /v1/login` 本科（swjw）：期望无 MFA 直接 200（密码已更新）。
2. `POST /v1/login` 研究生（`x-loginMode:1`）：期望直接 200。
3. `POST /v1/login/unified` 财务平台：type 2 自动完成 → 200。
4. `POST /v1/login/unified` 企微连接器：20003 + `method=otp`；`POST /v1/login/mfa` 6 位码 → 200。
5. `mfaMethod=sms`：20003 + `maskedTarget`；提交短信码 → 200。
6. `mfaMethod=wechat_qr`：20003 + `qrCodeUrl`/`qrImageBase64`；`GET /mfa/status` 202 → 扫码后 200。
7. `mfaMethod=wecom`：当前 app 预期 400 + 20007（不是代码 bug）。
8. 错误 OTP 5 次：401 → 429；会话删除后 410。
9. 密码过期：428 + 20004。
10. Redis 关闭：type 2 仍 200；交互式 503。
11. 改密后：旧密码继续 428/20004；新密码首次登录成功并写入新缓存；旧缓存键不可达/按 TTL 过期。

## 关键风险

| 风险 | 对策 |
|---|---|
| `UnifiedAuthFailure` 默认分支 502 | 新增枚举显式映射 + 守护测试 |
| Singleton/Scoped captive dependency | `SsoAuthService` 只依赖 Singleton（`ReAuthHandler`/`IMfaSessionStore`）；`MfaLoginService` Scoped |
| 限流重复消耗 | 首次登录只占一次 `CasAttemptGuard`；`/mfa` 不调用；MFA required 时 release 失败名额 |
| 缓存污染 | MFA 状态只在 `security:mfa:*`；成功后才写 edu/sso/unified 键 |
| 微信外部域名 SSRF | 硬编码 host、精确校验 302、独立 `MfaQrClient`、`UseProxy=false`、state 比对 |
| Redis 不可用 | 明确 503，不做进程内兜底；type 2 例外 |
| 旧客户端兼容 | 旧 `/Login` 200 + `MfaRequired`；密码过期 428 |
| type 4 当前不可用 | 实现分支；change code 0 → 400/20007；提交失败 401/20006，不误报 500 |
| type 9/14 未实测 | Provider 架构就绪；未知 host 安全失败；上线前实测 |
| 页面结构变化 | 正则逐字段解析；失败按上游错误 502，不伪装密码错误 |
| 密码/OTP/码泄漏 | 不落 Redis/日志；日志只记 session 前缀/哈希 |

## 关键文件

新增：
- `XAUAT.EduApi/Login/Mfa/`（`MfaMethod`、`MfaSeed`、`MfaPendingSession`、`MfaCacheKeys`、`IMfaSessionStore`、`MfaSessionStore`、`ReAuthPageParser`、`ReAuthHandler`、`QrProviderRegistry`、`MfaQrClient`、`MfaLoginService`）
- `XAUAT.EduApi/Controllers/V1/MfaController.cs`
- `EduApi.Data/Models/MfaChallenge.cs`、`MfaSubmitRequest.cs`、`MfaStatusResponse.cs`
- `XAUAT.EduApi/Exceptions/`（`MfaRequiredException`、`MfaFailedException`、`MfaSessionExpiredException`、`MfaUnavailableException`、`PasswordExpiredException`）

修改（核心）：
- `XAUAT.EduApi/Login/Xauat/XauatSsoClient.cs` / `IXauatSsoClient.cs` / `LoginToken.cs` / `XauatCookieJar.cs` / `XauatHttpSession.cs` / `XauatConstants.cs`
- `XAUAT.EduApi/Login/Unified/UnifiedSsoClient.cs` / `IUnifiedSsoClient.cs` / `UnifiedAuthService.cs`
- `XAUAT.EduApi/Login/SsoAuthService.cs` / `SsoLoginService.cs` / `ILoginRedisStore.cs` / `LoginRedisStore.cs`
- `XAUAT.EduApi/Interfaces/ILoginService.cs` / `IGraduateLoginService.cs` / `IUnifiedAuthService.cs`
- `XAUAT.EduApi/Services/GraduateLoginService.cs`
- `XAUAT.EduApi/Controllers/V1/LoginController.cs` / `UnifiedAuthController.cs` / `Controllers/Old/LoginController.cs`
- `XAUAT.EduApi/Extensions/ServiceCollectionExtensions.cs`
- `EduApi.Data/Models/LoginRequest.cs` / `ApiResponseModels.cs` / `UnifiedAuthRequest.cs` / `UnifiedAuthResponse.cs`
- `XAUAT.EduApi/Controllers/V1/ApiCodes.cs` / `Localization/ApiMessageKey.cs` / `ApiMessageLocalizer.cs`

测试：`Tests/Login/Mfa/`、`Tests/Controllers/MfaControllerTests.cs`，并扩展 `UnifiedSsoClientTests`、`UnifiedAuthServiceTests`、`SsoAuthServiceTests`、`SsoLoginServiceTests`、`GraduateLoginServiceTests`、`LoginModeControllerTests`、`ServiceRegistrationTests`、`ApiMessageLocalizerTests`、`TestSupport/*`。

## 验证

- `dotnet build` + `dotnet test` 全绿。
- 本地端到端（协议已实测）：
  - type 2：财务平台自动密码；
  - type 10：企微连接器 + Aegis `otpCode`；
  - type 3：企微连接器 + 短信 `dynamicCode`；
  - type 8：微信扫码 → `wx_errcode=405` → callback → ticket；
  - type 4：当前 app 返回 `MfaMethodUnsupported`/`reAuth_failed`，协议分支由 mock 覆盖。
- 生产按验证矩阵逐项打勾；先更新密码，再测 MFA。

---

## 实现状态（2026-11-14）

已完成：

- **DTO/错误码/本地化/异常**：`MfaChallenge`、`MfaSubmitRequest`、`MfaStatusResponse`、`MfaRequestOptions`；`ApiCodes.MfaRequired/PasswordExpired/MfaSessionExpired/MfaInvalidCode/MfaMethodUnsupported/MfaUnavailable`；`ApiMessageKey`/`ApiMessageLocalizer` 八语言；`MfaRequiredException`、`MfaFailedException`、`MfaSessionExpiredException`、`MfaUnavailableException`、`MfaMethodUnsupportedException`、`MfaBusyException`、`MfaProtocolException`、`PasswordExpiredException`。
- **Redis**：`ILoginRedisStore.SetIfNotExistsAsync/GetDeleteAsync`；`LoginCacheKeys.PasswordExpired`；`MfaCacheKeys`、`MfaPendingSession`、`MfaSessionStore`（5 分钟 TTL、SET NX 锁、轮询节流）。
- **协议层**：`MfaMethod`、`MfaSeed`、`ReAuthPageParser`、`ReAuthHandler`（type 2/3/4/5/8/10/11/12/13；type 8 微信扫码 + 轮询；type 9/14 架构预留）、`QrProviderRegistry`、`MfaQrClient`、`MfaChallengeCoordinator`。
- **标准链路**：`XauatSsoClient` 识别 re-auth/improveInfo；`SsoAuthService` type 2 自动完成、交互式 challenge、密码过期 flag/清缓存/释放限流；`SsoLoginService` 异常映射；`ILoginService`/`SsoLoginService` 新重载。
- **通用/研究生链路**：`UnifiedSsoClient` 识别并返回 MFA seed；`UnifiedAuthService` 处理 MFA/密码过期/flags；`GraduateLoginService` 透传选项。
- **控制器**：`/v1/login`、`/v1/login/unified` 返回 20003 + `data.mfa`；新增 `POST /v1/login/mfa`、`GET /v1/login/mfa/status`；旧 `/Login` 兼容；`PasswordExpired` 返回 428/20004。
- **DI**：`MfaSessionStore`/`ReAuthHandler`/`MfaChallengeCoordinator`/`MfaLoginService`/`MfaQrClient` 注册。
- **测试**：新增 `ReAuthPageParserTests`、`MfaSessionStoreTests`、`ReAuthHandlerTests`、`SsoAuthMfaTests`、`MfaControllerTests`；`FakeResponses.Json/Binary`。

验证：

- `dotnet build`：成功。
- `dotnet build -c Release`：成功。
- `dotnet test`：**624 passed, 0 failed**。

待验证/未覆盖：

- 生产真实端到端（需部署 + 把密码更新为新密码；按验证矩阵顺序）。
- type 9/14 扫码未实测（架构预留，未知 host 安全失败）。
- type 4 企业微信码发送/提交已实现，但当前 app 策略拒绝 4；需学校启用后验证。
- 微信扫码 type 8 的实现按实测协议编写，真实扫码需启动 API 后手工验证。
