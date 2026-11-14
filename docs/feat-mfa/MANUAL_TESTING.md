# MFA / re-auth 手动测试教程

本教程按「准备环境 → 冒烟 → 分类型测试 → 负向/降级 → 排查」的顺序，覆盖：

- `POST /v1/login`（本科 / 研究生）
- `POST /v1/login/unified`（通用统一认证）
- `POST /v1/login/mfa`（提交 OTP / 短信 / 企业微信验证码）
- `GET /v1/login/mfa/status`（微信扫码轮询）
- 密码过期（428 / 20004）与 Redis 降级

> 约定：所有示例使用占位符，**不要把真实学号、密码、OTP、短信码、二维码 code 提交到仓库或聊天记录**。
> 测试前建议先把密码改成没有在任何地方出现过的值。

---

## 目录

1. [测试准备](#1-测试准备)
2. [通用操作](#2-通用操作)
3. [基础回归](#3-基础回归)
4. [OTP（type 10）](#4-otp安全令牌type-10)
5. [短信（type 3）](#5-短信type-3)
6. [企业微信验证码（type 4）](#6-企业微信验证码type-4)
7. [微信扫码（type 8）](#7-微信扫码type-8)
8. [密码过期（428 / 20004）](#8-密码过期428--20004)
9. [方式切换与负向用例](#9-方式切换与负向用例)
10. [Redis 降级与限流](#10-redis-降级与限流)
11. [常见问题排查](#11-常见问题排查)
12. [完整测试清单](#12-完整测试清单)
13. [附录：请求 / 响应样例](#13-附录请求--响应样例)

---

## 1. 测试准备

### 1.1 环境要求

| 项 | 要求 |
|---|---|
| .NET | .NET 10 SDK |
| Redis | **交互式 MFA 必需**（OTP/短信/企微/扫码）；type 2 自动密码可不用 |
| 终端 | Windows PowerShell 5.1+ 或 PowerShell 7；也可用 Git Bash + curl |
| 扫码 | 手机微信（能扫二维码并点「确认登录」） |
| OTP | Aegis / 数盾 OTP 等标准 6 位 TOTP 认证器 |
| 短信 | 能接收学校绑定手机号的短信 |

### 1.2 启动 Redis

Docker：

```powershell
docker run -d --name eduapi-redis -p 6379:6379 redis:7-alpine
docker exec eduapi-redis redis-cli ping
```

本机已安装 Redis 时，确认 `redis-cli ping` 返回 `PONG` 即可。

> 没有 Redis 也能测：本科/研究生普通登录、财务平台 type 2 自动密码仍可用；
> 交互式 MFA（OTP/短信/企微/扫码）会在首次挑战时返回 `503 / 50004`。

### 1.3 配置 `.env`

`.env` 放在解决方案根目录，DotNetEnv 会自动发现（从工作目录向上查找）。最小配置：

```dotenv
ASPNETCORE_ENVIRONMENT=Development
PAYMENT_API_BASE_URL=http://localhost:1
REDIS=localhost:6379
SQL=
```

说明：

| 变量 | 作用 |
|---|---|
| `PAYMENT_API_BASE_URL` | 启动必需，随便一个非空地址即可（不测支付时） |
| `REDIS` | 交互式 MFA 必需；为空则跳过 Redis |
| `SQL` | 为空使用本地 SQLite |
| `LOG_VIEW_TOKEN` | 可选；设置后 `/Logs` 需要 `X-Log-Token` 请求头 |
| `UNIFIED_AUTH_ALLOWED_HOSTS` | 可选；追加通用认证允许的域名（默认 `*.edu.cn`） |

> `.env` 已在 `.gitignore` 中，**不要提交**。

### 1.4 构建与启动

```powershell
dotnet build
$env:ASPNETCORE_URLS = "http://localhost:5216"
dotnet run --project XAUAT.EduApi
```

或直接运行构建产物（工作目录设为 `XAUAT.EduApi`，保证 `.env` 能被向上找到）：

```powershell
dotnet XAUAT.EduApi\bin\Debug\net10.0\XAUAT.EduApi.dll --urls http://localhost:5216
```

### 1.5 确认服务就绪

```powershell
curl.exe -sS -o NUL -w "%{http_code}`n" http://localhost:5216/openapi/v1.json
```

返回 `200` 即就绪。浏览器打开：

- Scalar：`http://localhost:5216/scalar/v1`
- OpenAPI：`http://localhost:5216/openapi/v1.json`

### 1.6 准备测试账号与工具

- 使用自己的测试账号，密码必须是**改密后**的新密码。
- Aegis 里已绑定学校 OTP；手机能收短信；微信能扫码。
- 学校 CAS 提交限流 **5 次 / 20 分钟**，连续错密码还会触发验证码。
  **不要循环重试错误密码**，看到 429 按 `Retry-After` 等待。
- MFA 会话自身限制：**5 分钟 TTL、5 次失败**；扫码轮询有 1 秒节流、提交有 15 秒锁。

---

## 2. 通用操作

### 2.1 设置变量

```powershell
$Base = "http://localhost:5216"
$User = "你的学号"
$Pass = "你的新密码"
```

### 2.2 发送 JSON 请求

**推荐（成功路径，返回对象）**：

```powershell
$body = @{ username = $User; password = $Pass } | ConvertTo-Json
$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login" `
  -ContentType "application/json; charset=utf-8" -Body $body
$resp.code
$resp.data
```

**需要看 HTTP 状态 / 错误响应时用 curl**：

```powershell
curl.exe -sS -i -X POST "$Base/v1/login" `
  -H "Content-Type: application/json" `
  --data-binary "@$env:TEMP\eduapi-body.json"
```

PowerShell 5.1 下如果直接 `-d $json` 出现引号被吞，先把 body 写成无 BOM 的 UTF-8 文件：

```powershell
$bodyFile = Join-Path $env:TEMP "eduapi-body.json"
[IO.File]::WriteAllText($bodyFile, ($body | ConvertTo-Json -Depth 6),
  (New-Object Text.UTF8Encoding($false)))
```

### 2.3 响应结构

所有 `/v1` 接口统一返回：

```json
{
  "data": { },
  "code": 0,
  "message": "成功"
}
```

- `code = 0`：成功；
- `code = 20003`：需要二次认证，`data.mfa` 是挑战；
- 其他错误码见 2.4。

### 2.4 状态码 / 业务码速查

| HTTP | Code | 含义 |
|---:|---:|---|
| 200 | 0 | 成功 |
| 200 | 20003 | 需要二次认证；读 `data.mfa` |
| 202 | 20003 | 扫码等待中（`pending` / `scanned`） |
| 400 | 10001 | 参数错误 |
| 400 | 20007 | 当前应用不支持所选 MFA 方式 |
| 401 | 20002 | 用户名/密码错误 |
| 401 | 20006 | 验证码错误/上游拒绝 |
| 410 | 20005 | MFA 会话不存在、过期或尝试耗尽 |
| 428 | 20004 | 密码过期，需要先改密 |
| 429 | 40001 | 限流 / 并发忙；看 `Retry-After` |
| 502 | 50002 | 上游/网络/协议错误 |
| 503 | 50004 | 二次认证暂不可用（通常是 Redis 未配置） |

### 2.5 查看 MFA 日志

```powershell
curl.exe -sS "http://localhost:5216/Logs?level=Information&pageSize=100&search=MFA"
```

设置了 `LOG_VIEW_TOKEN` 时加请求头：

```powershell
curl.exe -sS -H "X-Log-Token: $env:LOG_VIEW_TOKEN" `
  "http://localhost:5216/Logs?level=Information&pageSize=100&search=MFA"
```

常看的日志：

- `MFA 启动：kind=... requested=... pageType=... service=...`
- `MFA 启动失败：failure=... message=...`
- `combinedLogin.do 未返回微信扫码跳转：HTTP ...，Location=...`
- `通用统一认证失败（MfaRequired）`

---

## 3. 基础回归

先确认非 MFA 路径没有回归，再测二次认证。

### 3.1 本科生 `/v1/login`

```powershell
$body = @{ username = $User; password = $Pass } | ConvertTo-Json
$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login" `
  -ContentType "application/json; charset=utf-8" -Body $body
$resp.code
$resp.data.cookie
```

**预期**：`HTTP 200`，`code=0`，`data.cookie` 非空。

- `401 + 20002`：密码错误（确认用的是改密后的新密码）。
- `428 + 20004`：密码过期，按第 8 节处理。
- `200 + 20003`：该账号/应用触发了 re-auth，按第 4~7 节继续。

### 3.2 研究生登录

```powershell
$headers = @{ "x-loginMode" = "1" }
$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login" `
  -Headers $headers -ContentType "application/json" -Body $body
$resp.code
$resp.data.cookie
```

**预期**：`HTTP 200`，`code=0`，`data.cookie` 为 gmis 会话。

### 3.3 通用统一认证 + 财务平台

财务平台的 `logCas` 是 SPA 入口，直接传它可能返回 `10001 NotCasProtected`。测试时用
**CAS 登录页 + service 参数**作为 `url`：

```powershell
$Service = "https://zhfwpt.xauat.edu.cn:8080/logCas"
$target = "https://authserver.xauat.edu.cn/authserver/login?service=" + [uri]::EscapeDataString($Service)

$body = @{
  url = $target
  username = $User
  password = $Pass
} | ConvertTo-Json

$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login/unified" `
  -ContentType "application/json; charset=utf-8" -Body $body
$resp.code
$resp.data.mfa.method
```

**预期（2026-11-14 实测）**：

- `200 + 20003`，`data.mfa.method = "wechat_qr"`，`data.mfa.reAuthType = 8`；
- 按第 7 节扫码完成。

如果学校某天把该应用改回 `reAuthType=2`（密码），API 会在首次请求内自动完成，直接返回
`200 + code=0`，不出现 20003。**行为取决于学校给应用配置的策略，不是代码分支。**

> 财务平台落地页 `logCas?ticket=ST-...` 是纯 SPA，**不写 cookie**；前端再调
> `GET /api/logcas/caslogintoken?cas=<ticket>` 换 token。因此 `data.cookie` 为空是目标站点行为，
> 不是 MFA 失败。`landingUrl` / `data.unified.landingUrl` 里的 ticket 才是关键。

### 3.4 财务平台 type 2（仅当页面策略为密码时）

页面 `reAuthType=2` 时，首次请求会：

1. 用 re-auth 页的 `pwdEncryptSalt` 再次加密密码；
2. `POST reAuthSubmit.do`；
3. 跟 `login?service=...` 拿 ticket。

**预期**：`HTTP 200`，`code=0`，无 20003。若页面已是 type 8，跳过本节。

---

## 4. OTP（安全令牌，type 10）

### 4.1 前提

OTP 能否测试取决于**当前应用页面是否提供 type 10**：

- 页面本身是 10：直接返回 OTP 挑战；
- 页面是 3/8，但 `changeReAuthType.do` 允许切到 10：API 会先切换再返回挑战；
- 页面只允许 8（例如 2026-11-14 的财务平台）：显式请求 `otp` 会得到 `400 / 20007`，
  **这是学校应用策略，不是代码 bug**。

### 4.2 触发挑战

```powershell
$body = @{
  url = $Target              # 3.3 里的 CAS 登录页 + service
  username = $User
  password = $Pass
  mfaMethod = "otp"
  trustDevice = $false
} | ConvertTo-Json

$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login/unified" `
  -ContentType "application/json; charset=utf-8" -Body $body

$resp.code
$resp.data.mfa
```

**预期**：

- `code = 20003`；
- `data.mfa.method = "otp"`；
- `data.mfa.reAuthType = 10`；
- `data.mfa.hint` 类似「请输入6位有效的安全令牌」；
- `data.mfa.sessionId` 非空。

保存 session：

```powershell
$SessionId = $resp.data.mfa.sessionId
```

### 4.3 提交 OTP

在 Aegis 里读**当前** 6 位码，立即提交（TOTP 30 秒滚动）：

```powershell
$body = @{
  mfaSessionId = $SessionId
  code = "Aegis当前6位码"
  trustDevice = $false
} | ConvertTo-Json

$result = Invoke-RestMethod -Method Post -Uri "$Base/v1/login/mfa" `
  -ContentType "application/json; charset=utf-8" -Body $body
$result.code
$result.data
```

**预期**：`code=0`，`data.cookie`（标准链路）或 `data.landingUrl` / `data.cookies`（统一链路）非空。

### 4.4 负向用例

| 操作 | 预期 |
|---|---|
| 提交错误 OTP | `401 / 20006` |
| 同一 session 连续错 5 次 | 第 5 次后会话删除；再提交 `410 / 20005` |
| 超过 5 分钟再提交 | `410 / 20005` |
| 提交不存在的 sessionId | `410 / 20005` |
| `mfaSessionId` 为空 | `400 / 10001` |
| 同一 session 并发提交 | 第二个请求 `429 / 40001`（15 秒锁） |
| 用 QR session 调 `/v1/login/mfa` | `400 / 20007`（扫码应走 status） |

> 错误 OTP 只计入 MFA 会话的 5 次上限，不额外消耗 CAS 的 5 次/20 分钟额度。

---

## 5. 短信（type 3）

### 5.1 触发并发送短信

```powershell
$body = @{
  url = $Target
  username = $User
  password = $Pass
  mfaMethod = "sms"
  trustDevice = $false
} | ConvertTo-Json

$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login/unified" `
  -ContentType "application/json; charset=utf-8" -Body $body

$resp.code
$resp.data.mfa.method
$resp.data.mfa.maskedTarget
$resp.data.mfa.sessionId
```

**预期**：

- `code = 20003`；
- `method = "sms"`；
- `maskedTarget` 是脱敏手机号（如 `155****9955`）；
- 手机会收到短信；`hint` 可能包含发送结果。

### 5.2 提交短信码

```powershell
$body = @{
  mfaSessionId = $resp.data.mfa.sessionId
  code = "短信里的验证码"
  trustDevice = $false
} | ConvertTo-Json

$result = Invoke-RestMethod -Method Post -Uri "$Base/v1/login/mfa" `
  -ContentType "application/json; charset=utf-8" -Body $body
$result.code
```

**预期**：`code=0`，返回最终会话。

### 5.3 短信发送失败 / 冷却

- 发送接口被限流时返回 `{"res":"code_time_fail","codeTime":...}`，API 映射为
  `429 / 40001` + `Retry-After`。
- 学校通道偶尔发不出短信；首次请求返回 502/发送失败时**等冷却时间再试**，不要连续点击。
- 短信码通常 120 秒有效；过期按 401/20006 处理，重新触发首次登录。

---

## 6. 企业微信验证码（type 4）

### 6.1 当前预期（2026-11-14）

```powershell
$body = @{
  url = $Target
  username = $User
  password = $Pass
  mfaMethod = "wecom"
  trustDevice = $false
} | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $body
```

**预期**：`400 / 20007`，`message = "当前应用不支持所选的二次认证方式"`。

原因：发送通道 `getDynamicCodeByReauth.do`（`authCodeTypeName=reAuthWChatDynamicCodeType`）
可以返回 `wechat_success`，但当前应用 `changeReAuthType.do` 对 type 4 返回 `code=0`，
即应用策略不接受企业微信验证码。**这不是代码 bug，重试也不会变。**

### 6.2 学校启用后怎么测

如果后续页面允许 type 4：

1. 首次请求应返回 `20003`，`method = "wecom"`；
2. 企业微信收到验证码；
3. 按 5.2 的方式提交 `code`（`dynamicCode` 分支与短信相同）。

---

## 7. 微信扫码（type 8）

### 7.1 触发扫码挑战

```powershell
$body = @{
  url = $Target
  username = $User
  password = $Pass
  mfaMethod = "wechat_qr"
  trustDevice = $false
} | ConvertTo-Json

$resp = Invoke-RestMethod -Method Post -Uri "$Base/v1/login/unified" `
  -ContentType "application/json; charset=utf-8" -Body $body

$resp.code
$mfa = $resp.data.mfa
$mfa.method
$mfa.reAuthType
$mfa.qrCodeUrl
$mfa.qrImageBase64.Length
$mfa.sessionId
```

**预期**：

- `code = 20003`；
- `method = "wechat_qr"`，`reAuthType = 8`，`isQr = true`；
- `qrImageBase64` 非空（原始 base64，不含 `data:` 前缀）；
- `qrCodeUrl` 形如 `https://open.weixin.qq.com/connect/qrcode/...`；
- `sessionId` 非空。

### 7.2 保存二维码并扫码

```powershell
$qrPath = Join-Path $env:TEMP "eduapi_mfa_qr.jpg"
[IO.File]::WriteAllBytes($qrPath, [Convert]::FromBase64String($mfa.qrImageBase64))
Start-Process $qrPath
```

用微信「扫一扫」打开图片中的二维码，并在手机上点「确认登录」。

> 二维码有时效（通常几分钟），API 侧 pending session 的 TTL 是 5 分钟。
> 过期后重新调 7.1 获取新二维码。

### 7.3 轮询扫码状态

```powershell
curl.exe -sS -i "$Base/v1/login/mfa/status?mfaSessionId=$($mfa.sessionId)"
```

| 阶段 | HTTP | Code | `data.status` | 处理 |
|---|---:|---:|---|---|
| 还没扫 | 202 | 20003 | `pending` | 等 1 秒再查 |
| 已扫未确认 | 202 | 20003 | `scanned` | 在手机上确认 |
| 已确认并登录 | 200 | 0 | `success` | 读 `data.login` / `data.unified` |
| 二维码过期 | 410 | 20005 | — | 重新走 7.1 |
| 轮询太快/并发 | 429 | 40001 | — | 按 `Retry-After` 等 |

说明：

- 该接口是**长轮询**，单次请求可能阻塞十几秒到 30 秒；curl 不要设置过短的超时
  （`MfaQrClient` 的超时是 35 秒）。
- 202 响应带 `Retry-After: 1`；轮询节流是 1 秒（Redis SET NX）。
- 扫码状态本身由微信返回：`408` 等待、`404` 已扫未确认、`405` 已确认、`403` 过期。

PowerShell 轮询循环（每 2 秒一次，最多 3 分钟）：

```powershell
$sid = $mfa.sessionId
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
  try {
    $r = Invoke-RestMethod -Uri "$Base/v1/login/mfa/status?mfaSessionId=$sid"
    $r.data.status
    if ($r.data.status -eq "success") { $r; break }
  } catch {
    $code = $_.Exception.Response.StatusCode.value__
    "HTTP $code"
    if ($code -eq 410) { break }
    if ($code -eq 429) { Start-Sleep -Seconds 2; continue }
  }
  Start-Sleep -Seconds 2
}
```

### 7.4 使用最终结果

扫码成功后：

- 统一链路：`data.unified`（`landingUrl`、`cookie`、`cookies`）；
- 标准链路：`data.login`（`cookie`、`studentId`）。

财务平台注意：`logCas` 是 SPA，不写 cookie；以 `landingUrl` 里的 `ticket=ST-...` 为准，
前端再调 `GET /api/logcas/caslogintoken?cas=<ticket>` 换 token。

> **落地页不可达**：生产到财务平台 `:8080` 可能 30 秒超时。扫码/回调已经拿到 ticket 时，
> 新版会返回 `200 / success`，`data.unified.landingUrl` 带 ticket，`data.unified.cookie` 为空；
> 这是降级成功，不是 500。旧版会返回 500/50002「服务器内部错误」。

### 7.5 扫码方式的负向用例

| 操作 | 预期 |
|---|---|
| 一直不扫，超过 5 分钟 | `410 / 20005`（会话删除） |
| 二维码被微信判定过期 | `410 / 20005` |
| 1 秒内连续轮询 | 后续请求可能 `429 / 40001` |
| 用 OTP session 调 status | 返回 `202 pending`（该 session 不是扫码会话，不会推进） |
| 扫码成功后再次轮询 | `410 / 20005`（成功后会话已删除） |

---

## 8. 密码过期（428 / 20004）

### 8.1 识别

```powershell
$body = @{ username = $User; password = $Pass } | ConvertTo-Json
curl.exe -sS -i -X POST "$Base/v1/login" `
  -H "Content-Type: application/json" -d $body
```

**预期**：

```json
{
  "data": {
    "success": false,
    "passwordExpired": true,
    "passwordChangeUrl": "https://authserver.xauat.edu.cn/authserver/improveInfo/improveUserInfo.do"
  },
  "code": 20004,
  "message": "当前密码已过期，请先修改统一身份认证密码"
}
```

HTTP 状态是 **428 Precondition Required**，不是 401。

### 8.2 怎么触发

密码过期由学校 90 天策略决定，**无法在测试里按需触发**。两种验证方式：

1. 用真实已过期账号（登录后 authserver 会 302 到 `improveInfo/improveUserInfo.do`）；
2. 依赖单元测试 `PasswordExpiredTests` / `MfaControllerTests` 覆盖 428 与缓存清理逻辑。

### 8.3 改密流程

1. 浏览器打开 `passwordChangeUrl`；
2. 用当前密码登录，按学校流程改密；
3. 客户端更新保存的密码；
4. 重新调用登录接口。

API **不会自动改密**：改密需要旧密码 + 新密码 + 确认密码，属于敏感操作。

### 8.4 缓存影响

检测到过期后，API 会：

- 写 `security:password_expired:{username}:{sha256(password)}`（24 小时）；
- 删除该密码哈希下的 `login-cookies-*`、`sso-cookies-*`、`unified:session:*`、`unified:ticket:*`；
- 释放失败名额（密码本身正确，不应计入失败）；
- 之后同一旧密码的缓存查询直接返回 428，避免被旧缓存「救活」。

改密后新密码的 SHA-256 不同，所有缓存键自然进入新命名空间，旧键按 TTL 过期。
**客户端必须同步更新密码**；API 不保存明文密码。

> 如果一直命中旧缓存、从未触发完整登录/换票，API 不会主动知道密码过期——这是缓存的检测延迟，
> 不是 bug。

---

## 9. 方式切换与负向用例

### 9.1 `mfaMethod` 取值

| 值 | 说明 |
|---|---|
| `auto` / 省略 | 使用页面默认方式 |
| `password` | type 2；只能用于页面本身就是密码的情况 |
| `otp` | type 10，6 位 TOTP |
| `sms` | type 3，短信 |
| `wecom` | type 4，企业微信 |
| `today_campus` | type 5，今日校园 |
| `wechat_qr` | type 8，微信扫码 |
| `qq_qr` | type 9，QQ 扫码（未实测） |
| `email` | type 11，邮箱 |
| `dingtalk` | type 12，钉钉 |
| `welink` | type 13，WeLink |
| `dingtalk_qr` | type 14，钉钉/企微/WeLink 扫码（未实测） |

常见别名：`pwd`、`weixin`/`wx`、`cpdaily`、`wechat_work`/`qy`、`qq`、`token`、`qr14`。

### 9.2 20007 的典型场景

| 场景 | 预期 |
|---|---|
| 应用只允许 8，却请求 `otp`/`sms`/`wecom` | `400 / 20007` |
| 页面是 8，显式请求 `password` | `400 / 20007`（不支持切回密码） |
| 用扫码 session 调 `POST /v1/login/mfa` | `400 / 20007` |
| 用 type 2 session 调 `POST /v1/login/mfa` | `400 / 20007`（密码在首次请求内完成） |
| `mfaMethod` 传未知字符串 | `400 / 20007` |

### 9.3 非法/过期会话

| 场景 | 预期 |
|---|---|
| `mfaSessionId` 为空 | `400 / 10001` |
| `mfaSessionId` 不存在 | `410 / 20005` |
| 会话超过 5 分钟 | `410 / 20005` |
| 连续 5 次验证码失败 | 会话删除，后续 `410 / 20005` |

### 9.4 并发

- `POST /v1/login/mfa` 对同一 session 有 15 秒 `SET NX` 锁；并发提交第二个请求 →
  `429 / 40001`。
- `GET /v1/login/mfa/status` 有 1 秒轮询节流；过快 → `429 / 40001`。

---

## 10. Redis 降级与限流

### 10.1 Redis 不可用

把 `.env` 的 `REDIS` 置空（或停掉 Redis），重启 API：

| 场景 | 预期 |
|---|---|
| 本科/研究生普通登录 | 正常 200 |
| 财务平台 type 2 自动密码（页面为 2 时） | 正常 200 |
| 首次请求遇到交互式 MFA（OTP/短信/企微/扫码） | `503 / 50004`（无法创建 pending session） |
| 用已有 session 调 `/v1/login/mfa` 或 `/status` | `410 / 20005`（Redis 不可用读不到会话，不是 503） |

### 10.2 CAS 提交限流

- 同一认证 host + 学号：**5 次 / 20 分钟**；
- 触发后 `429 / 40001` + `Retry-After`；
- 看到 429 不要立即重试，按响应头等待。

### 10.3 MFA 会话限制

- TTL **5 分钟**；
- 最多 **5 次**失败，超过后会话删除；
- 第二步不额外消耗 CAS 提交额度。

### 10.4 轮询 / 提交节流

- 扫码轮询：1 秒 SET NX，过快 → 429；
- 验证码提交：15 秒锁，并发 → 429；
- 扫码长轮询单次可能 30 秒左右，客户端超时应大于 35 秒。

---

## 11. 常见问题排查

| 现象 | 可能原因 | 处理 |
|---|---|---|
| 401 + `20002` | 用户名/密码错误 | 确认使用改密后的新密码 |
| 401 + `20006` | OTP/短信码错误或过期 | 重新获取码；OTP 30 秒、短信约 120 秒 |
| 410 + `20005` | 会话超过 5 分钟 / 5 次失败 / 已成功 | 重新走首次登录 |
| 400 + `20007` | 当前应用不支持该方式 | 换 `mfaMethod`，或等待学校启用 |
| 400 + `10001` | 参数错误；目标地址是 SPA 首页 | 改用 CAS 登录页 + `service` 作为 `url` |
| 428 + `20004` | 密码过期 | 浏览器改密后重试 |
| 429 + `40001` | CAS/MFA 限流、并发或轮询过快 | 按 `Retry-After` 等待 |
| 502 + `50002` | 上游不可达/协议异常 | 看 `/Logs`；确认网络与 authserver |
| 生产 502 / 约 30 秒超时，其他 `*.edu.cn` 正常 | 生产出网到 authserver **80 端口**被阻断 | 使用 HTTPS 登录页；2026-11 起代码默认 HTTPS，并自动把发现/回跳链里的 HTTP URL 升级 |
| 503 + `50004` | Redis 未配置/不可用（交互式挑战） | 配置 `REDIS` 后重启 |
| 200 + `20003` 但不是最终成功 | 这是 MFA 挑战 | 继续调 `/v1/login/mfa` 或轮询 status |
| 扫码一直 `pending` | 没扫/二维码过期 | 重新触发 `wechat_qr` |
| 扫码后一直 `scanned` | 手机没点「确认登录」 | 在微信里确认 |
| 扫码确认后 status 500 / 50002 | 最后落地页不可达（如财务 `:8080` 超时） | 新版会降级为 success + `landingUrl(ticket)`；若仍 500，看日志确认是哪一跳超时 |
| `combinedLogin.do 未返回微信扫码跳转` | re-auth 页 `service` 被 JSON 转义（`\/`）或 HTTPS origin 问题 | 确认 `ReAuthPageParser` 已反转义；看日志里的 HTTP/Location |
| 财务平台登录成功但 `cookie` 为空 | 财务落地页是 SPA，不写 cookie | 用 `landingUrl` 里的 ticket 换 token，不是 MFA 失败 |
| 企微连接器 `url` 返回 10001 | `/cas` 当前是空壳，不跳 CAS | 改用 CAS 登录页 + `service` |
| 第一次请求 503、日志有 `MFA 启动失败` | Redis 不可用 / 协议失败 / 应用拒绝方式 | 按 `failure` 字段区分：`Upstream`/`Protocol`/`MethodUnsupported` |

---

## 12. 完整测试清单

按顺序执行，每项记录实际 HTTP / code / 备注。

### 12.1 基础

- [ ] `GET /openapi/v1.json` → 200
- [ ] 本科 `POST /v1/login` → 200 / code 0 / `data.cookie` 非空
- [ ] 研究生 `POST /v1/login` + `x-loginMode: 1` → 200 / code 0
- [ ] 错误密码 → 401 / 20002（只试 1 次，避免限流）
- [ ] 密码过期账号 → 428 / 20004 + `passwordChangeUrl`

### 12.2 通用认证

- [ ] 财务平台 `POST /v1/login/unified` → 200 / 20003 + `wechat_qr`（当前策略）
- [ ] 若页面为 type 2：自动完成 → 200 / code 0
- [ ] SPA 入口直接作为 `url` → 400 / 10001（记录该行为）
- [ ] `mfaMethod=otp`（页面不支持时）→ 400 / 20007
- [ ] `mfaMethod=wecom` → 400 / 20007（当前策略）
- [ ] `mfaMethod=sms`（页面支持时）→ 20003 + `maskedTarget`

### 12.3 OTP

- [ ] 触发 `mfaMethod=otp` → 20003 / reAuthType 10 / sessionId
- [ ] 正确 OTP → 200 / code 0 / 最终 cookie 或 landingUrl
- [ ] 错误 OTP → 401 / 20006
- [ ] 连续 5 次错误 → 410 / 20005
- [ ] 超过 5 分钟 → 410 / 20005
- [ ] 不存在的 sessionId → 410 / 20005

### 12.4 短信

- [ ] 触发 `mfaMethod=sms` → 20003 + `maskedTarget`
- [ ] 收到短信 → 提交 `code` → 200 / code 0
- [ ] 发送冷却 → 429 / 40001 + `Retry-After`

### 12.5 微信扫码

- [ ] 触发 `mfaMethod=wechat_qr` → 20003 + `qrImageBase64` 非空
- [ ] 扫码前 `status` → 202 / `pending`
- [ ] 扫码后未确认 → 202 / `scanned`
- [ ] 手机确认 → 200 / `success` + `data.unified` 或 `data.login`
- [ ] 成功后再次 `status` → 410 / 20005
- [ ] 二维码过期 → 410 / 20005
- [ ] 1 秒内连续轮询 → 429 / 40001

### 12.6 负向 / 降级

- [ ] `mfaSessionId` 为空 → 400 / 10001
- [ ] 扫码 session 调 `/v1/login/mfa` → 400 / 20007
- [ ] type 2 session 调 `/v1/login/mfa` → 400 / 20007
- [ ] 同一 session 并发提交 → 429 / 40001
- [ ] Redis 置空后重启：type 2 仍 200；交互式首次挑战 503 / 50004
- [ ] Redis 置空后已有 session → 410 / 20005
- [ ] CAS 5 次/20 分钟 → 429 / 40001 + `Retry-After`

### 12.7 旧接口兼容（如仍在使用）

- [ ] 旧 `/Login` 交互式 MFA → 200 + `mfaRequired=true` + `mfa.sessionId`
- [ ] 旧 `/Login` 密码过期 → 428 + `passwordExpired=true`

---

## 13. 附录：请求 / 响应样例

### 13.1 OTP 挑战

```json
{
  "data": {
    "mfaRequired": true,
    "mfa": {
      "sessionId": "a1b2c3d4e5f6...",
      "method": "otp",
      "reAuthType": 10,
      "maskedTarget": "安全令牌",
      "qrCodeUrl": null,
      "qrImageBase64": null,
      "expiresAt": 1794480000,
      "message": "",
      "hint": "请输入6位有效的安全令牌",
      "isQr": false
    }
  },
  "code": 20003,
  "message": "需要二次认证"
}
```

### 13.2 扫码挑战

```json
{
  "data": {
    "mfaRequired": true,
    "mfa": {
      "sessionId": "a1b2c3d4e5f6...",
      "method": "wechat_qr",
      "reAuthType": 8,
      "maskedTarget": null,
      "qrCodeUrl": "https://open.weixin.qq.com/connect/qrcode/XXXXXXXX",
      "qrImageBase64": "/9j/4AAQSkZJRg...",
      "expiresAt": 1794480000,
      "message": "",
      "hint": "请使用微信扫码并在手机上确认",
      "isQr": true
    }
  },
  "code": 20003,
  "message": "需要二次认证"
}
```

### 13.3 第二步成功（统一链路）

```json
{
  "data": {
    "username": "你的学号",
    "studentId": "你的学号",
    "url": "https://<目标>",
    "authServerUrl": "https://authserver.xauat.edu.cn/authserver/login?service=...",
    "landingUrl": "https://<目标>?ticket=ST-...",
    "cookie": "k=v; k=v",
    "cookies": [
      { "host": "authserver.xauat.edu.cn", "name": "CASTGC", "value": "..." }
    ],
    "mfaRequired": false,
    "passwordExpired": false
  },
  "code": 0,
  "message": "成功"
}
```

### 13.4 扫码等待

```json
{
  "data": {
    "sessionId": "a1b2c3d4e5f6...",
    "status": "pending",
    "message": "等待扫码",
    "mfa": { "sessionId": "a1b2c3d4e5f6...", "method": "wechat_qr", "isQr": true }
  },
  "code": 20003,
  "message": "需要二次认证"
}
```

### 13.5 密码过期

```json
{
  "data": {
    "success": false,
    "studentId": "",
    "cookie": "",
    "mfaRequired": false,
    "passwordExpired": true,
    "passwordChangeUrl": "https://authserver.xauat.edu.cn/authserver/improveInfo/improveUserInfo.do"
  },
  "code": 20004,
  "message": "当前密码已过期，请先修改统一身份认证密码"
}
```

### 13.6 不支持的方式

```json
{
  "data": null,
  "code": 20007,
  "message": "当前应用不支持所选的二次认证方式"
}
```
