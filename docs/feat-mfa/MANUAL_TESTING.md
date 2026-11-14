# MFA / re-auth 手动测试指南

本文覆盖 `/v1/login`、`/v1/login/unified`、`/v1/login/mfa`、`/v1/login/mfa/status` 的手动验证。
所有示例使用占位符，**不要把真实学号/密码/OTP 提交到仓库**。

---

## 1. 准备

### 1.1 启动 API

```powershell
dotnet run --project XAUAT.EduApi
```

默认监听地址以启动日志为准；若没有，可显式指定：

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5000"
dotnet run --project XAUAT.EduApi
```

- Scalar UI：`http://localhost:5000/scalar/v1`
- OpenAPI：`http://localhost:5000/openapi/v1.json`

### 1.2 环境变量

`.env` 在解决方案根目录，DotNetEnv 会自动发现。至少确认：

| 变量 | 说明 |
|---|---|
| `PAYMENT_API_BASE_URL` | 启动必需，随便填一个非空地址即可（不测支付时） |
| `REDIS` | **交互式 MFA 必需**；为空时 type 2 自动密码仍可用，交互式会返回 503 |
| `SQL` | 为空使用本地 SQLite |

### 1.3 测试账号

- 使用你自己的测试账号；密码必须是**改密后**的新密码。
- 推荐先把密码改成未在聊天/文档中出现过的值，再写入测试命令。
- 学校有 CAS 提交限流（5 次/20 分钟）和验证码策略，**不要循环重试错误密码**。

---

## 2. 接口速查

| 接口 | 用途 |
|---|---|
| `POST /v1/login` | 本科生标准登录；`x-loginMode: 1` 为研究生 |
| `POST /v1/login/unified` | 通用统一认证：只给目标 URL + 学号密码 |
| `POST /v1/login/mfa` | 提交 OTP/短信/企业微信等验证码 |
| `GET /v1/login/mfa/status` | 扫码状态轮询 |

### 2.1 状态码 / 业务码

| HTTP | Code | 含义 |
|---:|---:|---|
| 200 | 0 | 登录完成 |
| 200 | 20003 | 需要二次认证；`data.mfa` 是挑战信息 |
| 202 | 20003 | 扫码等待中（pending/scanned） |
| 400 | 10001 | 参数错误 |
| 400 | 20007 | 当前应用不支持所选 MFA 方式 |
| 401 | 20002 | 用户名/密码错误 |
| 401 | 20006 | 验证码错误/上游拒绝 |
| 410 | 20005 | MFA 会话不存在、过期或尝试次数耗尽 |
| 428 | 20004 | 密码已过期，需要先改密 |
| 429 | 40001 | 限流/并发忙；看 `Retry-After` |
| 503 | 50004 | 二次认证暂不可用（通常是 Redis 不可用） |
| 502 | 50002 | 上游/网络/协议错误 |

### 2.2 MFA 挑战响应示例

```json
{
  "data": {
    "mfaRequired": true,
    "mfa": {
      "sessionId": "a1b2c3...",
      "method": "otp",
      "reAuthType": 10,
      "maskedTarget": "安全令牌",
      "qrCodeUrl": null,
      "qrImageBase64": null,
      "expiresAt": 1794480000,
      "message": "需要二次认证",
      "hint": "请输入6位有效的安全令牌",
      "isQr": false
    }
  },
  "code": 20003,
  "message": "需要二次认证"
}
```

> 收到 20003 只代表**第一次认证成功**，还没有拿到业务 cookie；必须再调 `POST /v1/login/mfa` 或轮询扫码。

---

## 3. 基础回归

### 3.1 本科生 `/v1/login`

```powershell
$Base = "http://localhost:5000"
$User = "你的学号"
$Pass = "你的新密码"

$json = @{ username = $User; password = $Pass } | ConvertTo-Json
curl.exe -sS -i -X POST "$Base/v1/login" `
  -H "Content-Type: application/json" -d $json
```

**预期**：`HTTP 200`，`code=0`，`data.cookie` 非空；不应出现 20003。

### 3.2 研究生登录

```powershell
curl.exe -sS -i -X POST "$Base/v1/login" `
  -H "Content-Type: application/json" `
  -H "x-loginMode: 1" -d $json
```

**预期**：`HTTP 200`，`code=0`，`data.cookie` 为 gmis 会话。

### 3.3 财务平台（type 2 自动密码）

```powershell
$json = @{
  url = "https://zhfwpt.xauat.edu.cn:8080/logCas"
  username = $User
  password = $Pass
} | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $json
```

**预期**：`HTTP 200`，`code=0`，直接返回 `data.cookie`。
财务平台的 re-auth 是 `reAuthType=2`，API 在首次请求内自动再次输入密码，所以**不会**返回 20003。

---

## 4. OTP（安全令牌，type 10）

### 4.1 触发挑战

```powershell
$json = @{
  url = "https://qiwei.xauat.edu.cn/cas"
  username = $User
  password = $Pass
  mfaMethod = "otp"
  trustDevice = $false
} | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $json
```

**预期**：

- `HTTP 200`，`code=20003`
- `data.mfa.method = "otp"`
- `data.mfa.reAuthType = 10`
- `data.mfa.sessionId` 非空
- `data.mfa.hint` 类似「请输入6位有效的安全令牌」

把 `sessionId` 记到变量：

```powershell
$SessionId = "上一步返回的 data.mfa.sessionId"
```

### 4.2 提交 OTP

在 Aegis 里读当前 6 位码，立即提交：

```powershell
$Code = "Aegis当前6位码"
$json = @{ mfaSessionId = $SessionId; code = $Code; trustDevice = $false } | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/mfa" `
  -H "Content-Type: application/json" -d $json
```

**预期**：`HTTP 200`，`code=0`，`data.cookie` 非空。

### 4.3 错误路径

- 错误 OTP → `HTTP 401`，`code=20006`。
- 同一 session 连续 5 次失败 → 会话删除；再提交返回 `410/20005`；重新走首次登录。
- 超过 5 分钟再提交 → `410/20005`。

---

## 5. 短信（type 3）

```powershell
$json = @{
  url = "https://qiwei.xauat.edu.cn/cas"
  username = $User
  password = $Pass
  mfaMethod = "sms"
  trustDevice = $false
} | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $json
```

**预期**：`20003`，`method="sms"`，`maskedTarget` 为脱敏手机号；短信会发到该手机号。
收到短信后按 4.2 调 `/v1/login/mfa`，把短信码放入 `code`。

> 短信有时会发送失败；若首次请求返回 502/发送失败，等冷却时间后重试，不要连续点击。

---

## 6. 企业微信验证码（type 4）

```powershell
$json = @{
  url = "https://qiwei.xauat.edu.cn/cas"
  username = $User
  password = $Pass
  mfaMethod = "wecom"
  trustDevice = $false
} | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $json
```

**当前账号/应用（2026-11-14 实测）预期**：

- `HTTP 400`，`code=20007`，`message=当前应用不支持所选的二次认证方式`
- 这是**服务端应用策略**拒绝 `reAuthType=4`，不是代码 bug。

如果学校后续给某个应用启用了企业号验证码，预期会变成：

- `20003`，`method="wecom"`；
- 企业微信收到验证码；
- 按 4.2 提交 `code`。

---

## 7. 微信扫码（type 8）

### 7.1 启动扫码

```powershell
$json = @{
  url = "https://qiwei.xauat.edu.cn/cas"
  username = $User
  password = $Pass
  mfaMethod = "wechat_qr"
  trustDevice = $false
} | ConvertTo-Json

curl.exe -sS -i -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $json
```

**预期**：

- `20003`
- `data.mfa.method = "wechat_qr"`
- `data.mfa.isQr = true`
- `data.mfa.qrImageBase64` 非空（原始 base64，不含 `data:` 前缀）
- `data.mfa.qrCodeUrl` 为二维码图片 URL

### 7.2 保存二维码并扫码

```powershell
$resp = curl.exe -sS -X POST "$Base/v1/login/unified" `
  -H "Content-Type: application/json" -d $json | ConvertFrom-Json

$SessionId = $resp.data.mfa.sessionId
[IO.File]::WriteAllBytes("$PWD\mfa_qr.jpg",
  [Convert]::FromBase64String($resp.data.mfa.qrImageBase64))
Start-Process "$PWD\mfa_qr.jpg"
```

用微信「扫一扫」打开图片中的二维码，并在手机上点「确认登录」。

### 7.3 轮询状态

```powershell
curl.exe -sS -i "$Base/v1/login/mfa/status?mfaSessionId=$SessionId"
```

| 阶段 | HTTP | `data.status` |
|---|---:|---|
| 还没扫 | 202 | `pending` |
| 已扫未确认 | 202 | `scanned` |
| 已确认并登录 | 200 | `success`（`data.login` 或 `data.unified` 为最终结果） |
| 二维码过期 | 410 | — |

扫码确认后，`data.login.cookie`（标准链路）或 `data.unified.cookie`（通用链路）就是最终会话。

---

## 8. 密码过期（428/20004）

用一个密码已过期（90 天策略）的账号登录：

```powershell
curl.exe -sS -i -X POST "$Base/v1/login" `
  -H "Content-Type: application/json" -d $json
```

**预期**：

```json
{
  "data": {
    "passwordExpired": true,
    "passwordChangeUrl": "https://authserver.xauat.edu.cn/authserver/improveInfo/improveUserInfo.do"
  },
  "code": 20004,
  "message": "当前密码已过期，请先修改统一身份认证密码"
}
```

处理方式：

1. 浏览器打开 `passwordChangeUrl`，用当前密码登录并按学校流程改密；
2. 客户端更新保存的密码；
3. 重新调用登录接口。

> 检测到过期后，API 会写 `security:password_expired:*` 并清掉旧密码哈希下的 eduCookie/SSO 票据/统一认证缓存；改密后密码哈希变化，会自动使用新的缓存命名空间。
>
> 如果一直命中旧缓存、从未触发完整登录/换票，API 不会主动知道密码过期；这是缓存的检测延迟，不是 bug。

---

## 9. 降级与限流

### 9.1 Redis 不可用

把 `.env` 的 `REDIS` 置空（或停掉 Redis）后重启 API：

- 本科生/研究生普通登录、财务平台 type 2 自动密码：仍应成功；
- 企微连接器交互式 MFA（OTP/短信/扫码）：首次请求返回 `503/50004`；
- `/v1/login/mfa`、`/v1/login/mfa/status`：`503/50004`。

### 9.2 限流

- 连续错误密码/验证码会触发 `CasAttemptGuard`（5 次/20 分钟）：
  `429/40001` + `Retry-After`。
- 看到 429 后不要立即重试，按 `Retry-After` 等待。
- MFA 第二步不会额外消耗 CAS 提交额度；但错误 OTP 会计入 MFA 会话的 5 次上限。

---

## 10. 常见问题排查

| 现象 | 可能原因 | 处理 |
|---|---|---|
| 401 + `code=20002` | 用户名/密码错误 | 确认使用改密后的新密码 |
| 401 + `code=20006` | OTP/短信码错误或过期 | 重新获取码；注意 30 秒/120 秒有效期 |
| 410 + `code=20005` | MFA 会话超过 5 分钟或 5 次失败 | 重新走首次登录 |
| 400 + `code=20007` | 当前应用不支持该 MFA 方式 | 换 `mfaMethod`（如 `otp`/`sms`），或等待学校启用 |
| 428 + `code=20004` | 密码过期 | 浏览器改密后重试 |
| 503 + `code=50004` | Redis 不可用 | 配置 `REDIS` 后重启 |
| 429 + `code=40001` | CAS/MFA 限流 | 按 `Retry-After` 等待 |
| 502 + `code=50002` | 上游不可达/协议异常 | 看 API 日志；确认网络/认证服务器 |
| 返回 200 但 `code=20003` | 这是 MFA 挑战，不是最终成功 | 继续调 `/v1/login/mfa` 或轮询扫码 |
| 扫码一直 `pending` | 二维码未扫/已过期 | 重新触发 `wechat_qr`；二维码有时效 |
| 扫码后 `scanned` 不变 | 手机未点「确认登录」 | 在微信里确认 |

---

## 11. 测试清单

- [ ] 本科生 `/v1/login` 200/code 0
- [ ] 研究生 `/v1/login` + `x-loginMode: 1` 200/code 0
- [ ] `/v1/login/unified` 财务平台 type 2 自动完成 200/code 0
- [ ] `/v1/login/unified` 企微连接器 `mfaMethod=otp` → 20003 → `/mfa` 200
- [ ] 错误 OTP → 401/20006
- [ ] `mfaMethod=sms` → 20003 + `maskedTarget` → `/mfa` 200
- [ ] `mfaMethod=wecom` → 当前预期 400/20007（记录实际结果）
- [ ] `mfaMethod=wechat_qr` → 20003 + 二维码 → `status` 202 → 扫码后 200
- [ ] 密码过期账号 → 428/20004 + `passwordChangeUrl`
- [ ] Redis 不可用 → type 2 仍 200；交互式 503/50004
- [ ] 连续失败 → 410/20005 或 429/40001
- [ ] 旧 `/Login`（如仍在使用）交互式 MFA → 200 + `mfaRequired=true` + `mfa.sessionId`
```
