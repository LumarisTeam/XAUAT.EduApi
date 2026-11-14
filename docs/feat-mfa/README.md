# XAUAT 统一身份认证 MFA / re-auth 说明

- 适用范围：`POST /v1/login`、`POST /v1/login/unified`、研究生登录（`x-loginMode: 1`）
- 配套文档：[详细手动测试教程](MANUAL_TESTING.md) · [学校通知截图](news.jpg)
- 最后更新：2026-11-14

---

## 1. 背景与结论

学校于 2026-11-12 起在统一身份认证平台启用多因子二次认证，通知里列出三种方式：

1. 企业微信验证码
2. 微信扫码
3. 安全令牌 OTP（标准 6 位 TOTP，可用 Aegis 等认证器）

实测结论：

- **学校没有给所有登录加全局 MFA**，而是按应用 / 按风险触发的 **step-up re-auth（二次认证）**：
  - 访问被配置为「强制二次认证」的应用（如财务综合服务平台、企微连接器）时触发；
  - 异地登录（`ipAnomaly > 0`）时触发；
  - 本科教务 `swjw`、研究生 `gmis` 不在强制列表，通常直接通过。
- **生产 `POST /v1/login` 的 401 直接原因是密码过期（90 天策略）**：authserver 把登录 302 到
  `/authserver/improveInfo/improveUserInfo.do`，旧代码不认识这个页面，统一报「用户名或密码错误」。
- **第二层原因是 re-auth**：密码改好后，如果访问强制应用或触发异地风控，authserver 会 302 到
  `/authserver/reAuthCheck/reAuthLoginView.do`，旧代码同样不认识。

当前实现已覆盖：

| 能力 | 状态 |
|---|---|
| 密码过期识别（428 / 20004 + 改密地址） | 已实现 |
| type 2 密码 re-auth（首次请求内自动完成） | 已实现，曾实测通过 |
| type 10 OTP（`otpCode`） | 已实现，实测通过 |
| type 3 短信（`dynamicCode`） | 已实现，实测通过 |
| type 4 企业微信验证码（`dynamicCode`） | 已实现；当前应用策略拒绝 4，待学校启用后验证 |
| type 8 微信扫码（二维码 + 轮询） | 已实现，端到端实测通过 |
| type 5/11/12/13 动态码 | 已实现（同一 `dynamicCode` 分支） |
| type 9/14 扫码 | 架构预留，未实测 |
| 两步式 MFA API（挑战 + 提交 / 扫码轮询） | 已实现 |

---

## 2. re-auth 协议

### 2.1 完整链路

```text
1. GET  /authserver/login                              → 200 登录页（pwdEncryptSalt / execution / lt）
2. POST /authserver/login                              → 302 /authserver/index.do（种下 CASTGC）
3. GET  /authserver/login?service=<受保护应用>          → 302 /authserver/reAuthCheck/reAuthLoginView.do?service=...
4. GET  /authserver/reAuthCheck/reAuthLoginView.do     → 200 re-auth 页，内联 var reAuthParams = {...}
5. POST /authserver/reAuthCheck/reAuthSubmit.do        → {"code":"reAuth_success","msg":"认证成功"}
6. GET  /authserver/login?service=<受保护应用>          → 302 <应用>?ticket=ST-...
```

第 5 步按 `reAuthType` 选择密码 / OTP / 动态码；微信扫码不走 `reAuthSubmit.do`，见 2.5。

### 2.2 `reAuthParams` 字段

页面内联的是 **JSON 对象字面量**（不是严格表单），字段示例：

```js
var reAuthParams = {
  isSleepAccount: "1",
  reAuthType: "2",
  isMultifactor: null,
  _isBindOtp: null,
  _otpUrl: null,
  ipAnomaly: 0,
  service: "https://zhfwpt.xauat.edu.cn:8080/logCas",
  reAuthUserId: "********",
  pwdEncryptSalt: "...",
  otpCodeRequired: "请输入6位有效的安全令牌"
};
```

| 字段 | 含义 |
|---|---|
| `service` | 当前受保护应用地址，提交 re-auth 时必须原样带回 |
| `reAuthType` | 当前二次认证方式，见 2.3 |
| `pwdEncryptSalt` | **re-auth 页自己的盐**，与登录页的盐不同 |
| `isMultifactor` | 是否多因子场景，通常为 `null` / 空 |
| `isSleepAccount` | `1` 时不展示「信任此设备」弹窗 |
| `ipAnomaly` | 异地登录标记，`>0` 时强制走 re-auth |
| `reAuthUserId` | 页面回填的用户标识 |
| `otpCodeRequired` | OTP 输入提示 |
| `_isBindOtp` | 是否已绑定 OTP |

> **重要实现细节：JSON 转义。** 线上页面会把 `service` 写成
> `https:\/\/zhfwpt.xauat.edu.cn:8080\/logCas`。正则捕获后必须按 JSON 字符串规则反转义，
> 否则 `combinedLogin.do` 的 `success` 参数会变成 `https%3A%5C%2F%5C%2F...`，
> 上游返回 200 错误页而不是微信 302。`ReAuthPageParser.UnescapeJsonString` 负责这一步。

### 2.3 `reAuthType` 取值

| 值 | 方式 | API `mfaMethod` | 备注 |
|---:|---|---|---|
| 2 | 再次输入密码 | `password` | 首次请求内自动完成 |
| 3 | 短信验证码 | `sms` | `dynamicCode` |
| 4 | 企业微信验证码 | `wecom` | 当前应用策略拒绝 |
| 5 | 今日校园验证码 | `today_campus` | `dynamicCode` |
| 8 | 微信扫码 | `wechat_qr` | 二维码 + 轮询 |
| 9 | QQ 扫码 | `qq_qr` | 架构预留，未实测 |
| 10 | 安全令牌 OTP | `otp` | 6 位 TOTP |
| 11 | 邮箱验证码 | `email` | `dynamicCode` |
| 12 | 钉钉验证码 | `dingtalk` | `dynamicCode` |
| 13 | WeLink 验证码 | `welink` | `dynamicCode` |
| 14 | 钉钉/企微/WeLink 扫码 | `dingtalk_qr` | 架构预留，未实测 |

`mfaMethod` 还接受 `auto`（默认，按页面类型）以及常见别名（`pwd`、`weixin`、`wx`、`cpdaily` 等）；
请求了页面不支持且无法切换的方式 → `400 / 20007`，不会静默回落到其他方式。

### 2.4 提交字段与响应

`POST /authserver/reAuthCheck/reAuthSubmit.do`（form-urlencoded）固定字段：

```text
service=<受保护应用>
reAuthType=<2|3|4|5|10|11|12|13>
isMultifactor=<页面值，通常为空>
password=<AES(password, reAuthParams.pwdEncryptSalt)>   # type 2
otpCode=<6 位数字>                                      # type 10
dynamicCode=<短信/企微/邮箱等验证码>                        # type 3/4/5/11/12/13
uuid=""
answer1=""
answer2=""
skipTmpReAuth=<true|false>                              # TrustDevice
```

成功：`{"code":"reAuth_success","msg":"认证成功"}`
失败：`{"code":"reAuth_failed"|"reAuth_unauthorized","msg":"..."}`

成功后调用方再 `GET /authserver/login?service=...`，CAS 返回 `302 <应用>?ticket=ST-...`。

切换方式：`POST /authserver/reAuthCheck/changeReAuthType.do`，只有响应 `code == "1"` 才表示页面接受目标方式；
`code == "0"` → 该应用不支持，API 映射为 `400 / 20007`。

### 2.5 动态码发送

`POST /authserver/dynamicCode/getDynamicCodeByReauth.do`：

```text
userName=<reAuthUserId 或学号>
authCodeTypeName=<按方式>
```

| 方式 | `authCodeTypeName` | 成功响应示例 |
|---|---|---|
| 短信 | `reAuthDynamicCodeType` | `{"res":"success","mobile":"155****9955","codeTime":120}` |
| 企业微信 | `reAuthWChatDynamicCodeType` | `{"res":"wechat_success","returnMessage":"验证码已发送成功"}` |
| 今日校园 | `reAuthCpdailyDynamicCodeType` | 同上架构 |
| 邮箱 | `reAuthEmailDynamicCodeType` | 同上架构 |
| 钉钉 | `reAuthDingTalkDynamicCodeType` | 同上架构 |
| WeLink | `reAuthWeLinkDynamicCodeType` | 同上架构 |

限流响应：`{"res":"code_time_fail","codeTime":...}`，API 映射为 429 + `Retry-After`。

### 2.6 微信扫码

```text
GET /authserver/combinedLogin.do?type=weixin&reAuth=1&success=<service>
  → 302 https://open.weixin.qq.com/connect/qrconnect?appid=...&redirect_uri=...&state=...
GET 微信 qrconnect 页
  → 解析二维码 UUID
GET https://open.weixin.qq.com/connect/qrcode/<uuid>        # 二维码图片
GET https://lp.open.weixin.qq.com/connect/l/qrconnect?uuid=<uuid>&_=<ts>   # 长轮询
  → wx_errcode=408 等待扫码
  → wx_errcode=404 已扫码未确认
  → wx_errcode=405 + wx_code=<code> 已确认
  → wx_errcode=403 二维码过期
GET /authserver/callback?code=<wx_code>&state=<state>       # 用 authserver 会话
  → 302 /authserver/login?service=<service>
GET /authserver/login?service=<service>
  → 302 <service>?ticket=ST-...
```

API 只允许跳转到硬编码的 `open.weixin.qq.com` / `lp.open.weixin.qq.com`，并校验 `state` 来自
`combinedLogin.do` 的 302；未知 302 host 一律按协议错误处理。

> **HTTPS 优先**：2026-11 实测生产出网到 authserver 的 **80 端口会超时、443 正常**；
> 代码默认使用 `https://authserver.xauat.edu.cn`，并会把统一认证发现/回跳链里的 authserver
> HTTP URL 升级为 HTTPS。re-auth 端点（`combinedLogin.do` / `callback`）本身也必须走 HTTPS，
> 否则第一跳是 authserver→HTTPS，会被误判成「不是微信跳转」。

### 2.7 密码过期

触发：所有 service 登录都 302 到 `/authserver/improveInfo/improveUserInfo.do`（标题「完善资料」）。

- `POST /authserver/improveInfo/updateUserInfo.do` 返回：
  `{"errMsg":"当前密码已过期。为了您的账号安全，管理员要求您90天修改一次密码"}`
- 改密接口：`POST /authserver/improveInfo/improveInfoUpdatePassword.do`，
  参数 `oldPassword` / `newPassword` / `confirmPassword`，使用固定盐 `q1hVinmcu2i7fPyF` 做 AES。
- **API 不自动改密**：改密需要旧密码 + 新密码 + 确认密码，是敏感操作；返回 428 + 改密 URL，
  由用户在浏览器完成，客户端更新保存的密码后重试。

### 2.8 「信任此设备」

- 只有页面 `isSleepAccount === '0'` 时才展示「信任此设备」弹窗。
- 勾选信任 → `skipTmpReAuth=true`；仅本次登录 → `false`。API 的 `trustDevice` 默认 `true`。
- 当前账号 `isSleepAccount=1` 时该选项不生效，`queryAllDevices` 仍为空；**不能把信任设备当作免 MFA 的修复手段**。

### 2.9 限流

- 学校 CAS 提交限流：同一认证 host + 学号，**5 次 / 20 分钟**。
- 应用自身 `CasAttemptGuard` 同样按 5 次 / 20 分钟计数；触发后返回 429 + `Retry-After`。
- MFA 第二步**不额外消耗** CAS 提交额度；但错误 OTP / 动态码计入 MFA 会话的 5 次上限。
- 验证码错误次数过多会触发学校验证码策略；不要循环重试错误密码。

---

## 3. 旧代码为什么失败

### 3.1 标准 `/v1/login`

`XauatSsoClient.InterpretLoginResponseAsync` 只把 301/302 当作 SSO 票据，不检查 `Location`
是不是 re-auth / improveInfo 页面；200 页面只认 `span#msg`，re-auth / 改密页没有该元素，
最终统一变成「用户名或密码错误」→ 401。

`ExchangeSsoTicketAsync` 跟跳转时只检查最后一跳 `StatusCode == 200`，re-auth 页也是 200，
于是拿 re-auth 会话去当教务会话。

### 3.2 通用 `/v1/login/unified`

`SubmitCredentialsAsync` / `FollowBackAsync` 同样不识别 `reAuthCheck` 和 `improveInfo`；
`UnifiedAuthFailure` 也没有 `MfaRequired` / `PasswordExpired` 分类，全部落到
`InvalidCredentials` 或 `Upstream`。

### 3.3 生产 401 的判定顺序

1. 先看是否 `improveInfo`（密码过期）——优先级最高，因为根本走不到 MFA；
2. 再看是否 `reAuthCheck`（二次认证）；
3. 最后才是真正的凭据错误。

旧代码把 1、2 都误判成 3。

---

## 4. API 契约

### 4.1 首次请求遇到 MFA

`POST /v1/login` 与 `POST /v1/login/unified` 返回 **HTTP 200 + `code=20003`**，`data` 仍是
`LoginResponse` / `UnifiedAuthResponse`，并新增：

```csharp
public bool MfaRequired { get; set; }
public MfaChallenge? Mfa { get; set; }
public bool PasswordExpired { get; set; }
public string? PasswordChangeUrl { get; set; }
```

`MfaChallenge` 字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `sessionId` | string | 一次性会话 ID，不含账号/密码/验证码 |
| `method` | string | `password` / `otp` / `sms` / `wecom` / `wechat_qr` 等 |
| `reAuthType` | int | authserver 的 `reAuthType` |
| `maskedTarget` | string? | 脱敏手机号/邮箱/姓名 |
| `qrCodeUrl` | string? | 二维码图片 URL |
| `qrImageBase64` | string? | 二维码图片原始 base64（无 `data:` 前缀） |
| `expiresAt` | long | 会话过期时间（Unix 秒） |
| `message` | string | 通常为空；提示文案在顶层 `message` |
| `hint` | string? | 上游输入提示，如「请输入6位有效的安全令牌」 |
| `isQr` | bool | 是否扫码方式；扫码走状态轮询 |

首次请求 DTO 新增：

| 字段 | 默认 | 说明 |
|---|---|---|
| `mfaMethod` | `auto` | 页面默认方式；也可显式指定 |
| `trustDevice` | `true` | 对应 `skipTmpReAuth` |

示例（OTP 挑战）：

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
      "message": "",
      "hint": "请输入6位有效的安全令牌",
      "isQr": false
    }
  },
  "code": 20003,
  "message": "需要二次认证"
}
```

> 收到 20003 只代表**第一次认证成功**，还没有拿到业务会话；必须继续调
> `POST /v1/login/mfa` 或轮询 `/v1/login/mfa/status`。

### 4.2 第二步：提交验证码

`POST /v1/login/mfa`

```json
{
  "mfaSessionId": "a1b2c3...",
  "code": "123456",
  "trustDevice": false
}
```

- 成功：HTTP 200 + `code=0` + 最终 `LoginResponse` / `UnifiedAuthResponse`。
- 扫码会话或 type 2 会话调用本接口 → `400 / 20007`（扫码请用状态轮询）。
- 错误码见 4.4。

### 4.3 第二步：扫码轮询

`GET /v1/login/mfa/status?mfaSessionId=...`

| 阶段 | HTTP | `code` | `data.status` |
|---|---:|---:|---|
| 还没扫 | 202 | 20003 | `pending` |
| 已扫未确认 | 202 | 20003 | `scanned` |
| 已确认并登录 | 200 | 0 | `success`（`data.login` 或 `data.unified`） |
| 二维码过期 / 会话过期 | 410 | 20005 | — |
| 轮询过快 / 并发 | 429 | 40001 | — |

响应头带 `Retry-After: 1`（202 时）。扫码方式是**长轮询**，单次请求可能阻塞十几秒到 30 秒，
客户端超时应大于 35 秒。

> **落地页不可达时的降级**：如果扫码/回调已经拿到 `ticket=ST-...`，只是最后访问业务落地页
> （例如生产到财务平台 `:8080` 超时）失败，统一认证会返回 `success`，`landingUrl` 保留带
> ticket 的地址，`cookie` 可能为空。调用方应把用户引导到该 `landingUrl` 完成落地；这不是
> MFA 失败，也不会再返回 500。

### 4.4 错误码 / HTTP

| 场景 | HTTP | Code |
|---|---:|---:|
| 成功 | 200 | 0 |
| 需要二次认证 | 200 | 20003 |
| 扫码等待中 | 202 | 20003 |
| 用户名/密码错误 | 401 | 20002 |
| 密码过期 | **428** | 20004 |
| MFA 会话不存在/过期/尝试耗尽 | 410 | 20005 |
| 验证码错误/上游拒绝 | 401 | 20006 |
| MFA 方式不支持 | 400 | 20007 |
| 参数错误 | 400 | 10001 |
| 限流 / 并发忙 | 429 | 40001 |
| 上游/网络/协议错误 | 502 | 50002 |
| MFA 暂不可用（Redis 不可用） | 503 | 50004 |

`MfaRequiredException` / `PasswordExpiredException` / `MfaUnavailableException` 必须在
`LoginFailedException` **之前**捕获；密码过期不继承 `LoginFailedException`，避免被旧 catch 吞掉。

旧 `/Login` 兼容：交互式 MFA 返回 200 + `LoginResponse{ success:false, mfaRequired:true, mfa:... }`；
密码过期返回 428。

---

## 5. 实现结构

### 5.1 `XAUAT.EduApi/Login/Mfa/`

| 文件 | 职责 |
|---|---|
| `MfaMethod.cs` | 枚举、`TryParse`、`reAuthType` / `authCodeTypeName` 映射 |
| `MfaSeed.cs` | 首次登录结果里的内部载荷（kind、service、reAuthType、salt、cookies、缓存键等） |
| `MfaPendingSession.cs` | Redis 待认证会话；**不存明文密码/OTP/动态码/二维码 code** |
| `MfaCacheKeys.cs` | `security:mfa:{id}`、`security:mfa:lock:{id}`、`security:mfa:poll:{id}` |
| `IMfaSessionStore` / `MfaSessionStore` | 会话 CRUD、5 分钟 TTL、SET NX 锁 15s、轮询节流 1s |
| `ReAuthPageParser.cs` | 识别/解析 `reAuthParams`，JSON 反转义，密码过期页识别 |
| `ReAuthHandler.cs` | `StartAsync` / `CompleteAsync` / `PollQrAsync`，切换方式、发送动态码、二维码、跟票 |
| `QrProviderRegistry.cs` | 扫码 provider：微信已实测；QQ/钉钉预留 |
| `MfaQrClient.cs` | 只访问微信硬编码域名的独立客户端（无 cookie、无代理、35s 超时） |
| `MfaChallengeCoordinator.cs` | 首次登录遇到 re-auth 的统一编排（三条链路共用） |
| `MfaLoginService.cs` | 第二步编排：加载会话、恢复 jar、提交/轮询、写缓存、返回最终结果 |

### 5.2 三个接入点

**标准 `/v1/login`**

- `XauatSsoClient` 识别 302/200 的 re-auth / improveInfo；
- `SsoAuthService`：type 2 自动完成；交互式创建 pending session 并返回 `MfaRequired`；
  密码过期写 flag、清缓存、释放限流名额；
- `SsoLoginService` 映射为对应异常；
- MFA 第二步不调用 `CasAttemptGuard`。

**通用 `/v1/login/unified` 与研究生**

- `UnifiedSsoClient` 在 `SubmitCredentialsAsync` / `FollowBackAsync` 识别 re-auth / improveInfo；
- `UnifiedAuthService` 显式处理 `MfaRequired` / `PasswordExpired` / `MfaUnavailable`；
- `GraduateLoginService` 透传 `mfaMethod` / `trustDevice`；
- 成功后用 seed 里的缓存键写 `unified:session:*`（2 分钟）/ `unified:ticket:*`（1 天）。

**控制器**

- `LoginController` / `UnifiedAuthController`：20003、428、503、400/20007、401/20006、410、429；
- 新增 `Controllers/V1/MfaController.cs`：`POST /v1/login/mfa`、`GET /v1/login/mfa/status`；
- 旧 `Controllers/Old/LoginController.cs`：兼容旧客户端。

### 5.3 密码过期与持久化登录

标准链路缓存优先级：

1. `login-cookies-{user}-{sha256(password)}`（eduCookie，20 分钟）
2. `sso-cookies-{user}-{sha256(password)}`（CASTGC，7 天）
3. 完整登录

关键行为：

- **缓存命中时 API 不访问 authserver**，因此不知道密码已过期；已登录会话可以继续用，
  直到 TTL（eduCookie 20 分钟，SSO 票据 7 天）。这是缓存的检测延迟，不是 bug。
- 一旦检测到过期（完整登录或换票被拦到 `improveInfo`）：
  - 写 `security:password_expired:{username}:{sha256(password)}`，TTL 24 小时；
  - 删除该密码哈希下的 `login-cookies-*`、`sso-cookies-*`、`unified:session:*`、`unified:ticket:*`；
  - 释放失败名额（密码本身正确，不应计入失败）；
  - 之后同一旧密码的缓存查询先检查 flag，命中直接返回 428，避免被旧缓存「救活」。
- **用户改密后**：新密码 SHA-256 不同，所有缓存键自然切换到新命名空间；旧键按 TTL 过期。
  客户端必须更新保存的密码；API 不保存明文密码。
- 通用链路同样在 `ReadSessionAsync` / 票据复用前检查 flag。

### 5.4 安全设计

- 密码、OTP、短信码、企业微信码只在内存与表单中出现；不写 Redis、不写日志、不进 URL。
- pending session 只存 `PasswordHash`（SHA-256），用于复刻既有缓存键。
- 二维码只允许 `open.weixin.qq.com` / `lp.open.weixin.qq.com`；302 目标精确校验 host/path；
  `state` 来自 authserver 的 302；`MfaQrClient` 关闭代理并保留内网地址闸门。
- 解析失败按上游协议错误（502）处理，不伪装成密码错误。

---

## 6. 配置与部署

| 变量 | 说明 |
|---|---|
| `PAYMENT_API_BASE_URL` | 启动必需，非空即可（不测支付时） |
| `REDIS` | **交互式 MFA 必需**；为空时 type 2 自动密码仍可用，交互式返回 503 |
| `SQL` | 为空使用本地 SQLite |
| `LOG_VIEW_TOKEN` | 可选；保护 `/Logs`，设置后请求需带 `X-Log-Token` |

启动：

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5216"
dotnet run --project XAUAT.EduApi
```

- Scalar：`http://localhost:5216/scalar/v1`
- OpenAPI：`http://localhost:5216/openapi/v1.json`

生产部署前务必：

1. 把客户端保存的密码更新为改密后的新密码；
2. 确认 Redis 可用（交互式 MFA 强依赖）；
3. 等 `CasAttemptGuard` 的 `Retry-After` 窗口重置，不要循环重试；
4. 按第 7 节验证矩阵逐项打勾。

---

## 7. 验证状态

### 7.1 自动化测试

- `dotnet build`：成功；
- `dotnet test`：**627 passed, 0 failed**（2026-11-14）；
- MFA + 统一认证客户端子集：**44 passed, 0 failed**。

覆盖 `ReAuthPageParser`、`MfaSessionStore`、`ReAuthHandler`、`SsoAuthMfa`、`MfaController`，
以及各链路已有的登录/缓存/限流测试。

### 7.2 本地端到端实测

| 场景 | 结果 |
|---|---|
| type 10 OTP（企微连接器） | Aegis 6 位码 → `reAuth_success` → `ticket=ST-...` |
| type 3 短信（企微连接器） | 发送成功 → `dynamicCode` → `reAuth_success` → ticket |
| type 8 微信扫码（财务平台） | `20003` 二维码 → 扫码 → `202 scanned` → `200 success` → `landingUrl` 带 ticket |
| type 2 密码（财务平台，历史策略） | 首次请求内自动完成 → `200 code=0` |
| type 4 企业微信（当前策略） | 发送通道可用，但 `changeReAuthType(4)` 返回 code 0 → `400/20007` |
| 密码过期 | 识别 `improveInfo` → 428/20004 + 改密地址 |

### 7.3 已知限制 / 未覆盖

- **type 9 / 14 扫码未实测**；provider 架构就绪，未知 host 会安全失败。
- **type 4 需学校启用**；当前应用策略拒绝 4，协议分支由单元测试覆盖。
- **服务端 TOTP（`otpSecret`）刻意未实现**：OTP 只支持调用方每次传入 `otpCode`；
  `otpSecret` 等同长期凭据，不落库、不落 Redis、不落日志。
- **让生产服务器走中国大陆出口 IP 只能规避 `ipAnomaly` 风控**，不能替代 re-auth 处理，
  也不保证学校不调整策略。
- **财务平台落地页是纯 SPA，不写 cookie**：`logCas?ticket=ST-...` 返回 200 HTML，
  前端再调 `GET /api/logcas/caslogintoken?cas=<ticket>` 换 token。因此 `data.cookie` 为空
  是目标站点行为，不是 MFA 失败（普通非 MFA 统一登录同样如此）。
- **企微连接器 `/cas` 入口当前是空壳**（`casAuthUrl` 为空），不再跳 CAS；
  测试时改用 CAS 登录页 + `service` 参数作为 `url`。
- **生产真实端到端**需部署后按验证矩阵执行；本机无法完全复现生产出口 IP 的 `ipAnomaly`。
- **缓存检测延迟**：长期命中缓存且从不触发完整登录/换票时，API 不会主动知道密码过期。

### 7.4 生产网络：authserver 80 端口不可达（2026-11-14）

生产 `https://xauatapi.xauat.site` 实测：

- `http://authserver.xauat.edu.cn` 从生产出网 **30 秒超时**，`https://authserver.xauat.edu.cn` 正常；
- 其他 `*.edu.cn`（清华、西建大主站）快速可达，说明不是整体出网故障；
- `/v1/login` 把上游失败映射为「用户名或密码错误」，表现为快速 401；
  `/v1/login/unified` 表现为 502；
- 用 `https://authserver.xauat.edu.cn/authserver/login?...` 作为 `url` 的统一认证在生产已成功
  （200/code 0，拿到 swjw cookie）。
- 财务平台 `https://zhfwpt.xauat.edu.cn:8080` 从生产同样 30 秒超时；扫码确认后旧实现会在
  最后落地跳转抛 `TaskCanceledException` → 500。现在跟票链在「已拿到 ticket、落地页不可达」时
  返回 `success` + `landingUrl(ticket)`。

修复：`XauatConstants.AuthServerUrl` 改为 HTTPS；`XauatConstants.SecureAuthServer` 在标准链路
重定向、re-auth 跟票与统一认证发现/回跳的每一跳把 authserver HTTP URL 升级为 HTTPS。
部署后需重新验证 `/v1/login`、`/v1/login/unified` 和扫码 status。

---

## 8. 维护提示

### 8.1 新增加密/认证方式

1. 在 `MfaMethod` 加枚举 + `FromReAuthType` + `ToApiName` +（如需要）`GetAuthCodeTypeName`；
2. 在 `ReAuthHandler.StartAsync` 的 switch 增加分支（密码/动态码/扫码）；
3. 扫码方式在 `QrProviderRegistry` 注册 provider，并在 `StartQrAsync` 里校验 host/path；
4. 补 `MfaMethod` 映射、`ReAuthHandler`、控制器测试；
5. 更新本文第 2.3 节与手动测试教程。

### 8.2 页面结构变化

`reAuthParams` 由学校前端生成，字段可能随升级变化。解析器逐字段正则提取；缺字段时返回
`MfaProtocolException`（502），不会退化成密码错误。新增字段时先抓一份页面 HTML 再加规则。

### 8.3 日志

- `MFA 启动：kind=... requested=... pageType=... service=...`
- `MFA 启动失败：failure=... message=...`
- `combinedLogin.do 未返回微信扫码跳转：HTTP ...，Location=...`
- `通用统一认证失败（...）`

日志不包含密码、OTP、动态码；`service` / `landingUrl` 可能出现，但不含凭据。

---

## 9. 附录

### 9.1 关键端点

| 端点 | 方法 | 用途 |
|---|---|---|
| `/authserver/login` | GET/POST | 一次认证（账号密码） |
| `/authserver/reAuthCheck/reAuthLoginView.do` | GET | 二次认证页面 |
| `/authserver/reAuthCheck/reAuthSubmit.do` | POST | 提交二次认证 |
| `/authserver/reAuthCheck/changeReAuthType.do` | POST | 切换二次认证方式 |
| `/authserver/dynamicCode/getDynamicCodeByReauth.do` | POST | 发送短信/企微/邮箱等动态码 |
| `/authserver/combinedLogin.do` | GET | 启动微信/QQ 扫码 |
| `/authserver/callback` | GET | 微信扫码回调 |
| `/authserver/improveInfo/improveUserInfo.do` | GET | 密码过期/完善资料页 |
| `/authserver/improveInfo/updateUserInfo.do` | POST | 「我已完成」；过期时返回明确 errMsg |
| `/authserver/improveInfo/improveInfoUpdatePassword.do` | POST | 改密（固定盐 `q1hVinmcu2i7fPyF`） |
| `/personalInfo/accountRelation/userSettingDetail` | POST | 读取账号 re-auth 策略 |
| `/personalInfo/otpToken/isBind` | POST | 查询 OTP 绑定状态 |
| `/personalInfo/accountSecurity/queryAllDevices` | GET | 可信设备列表 |

### 9.2 关键文件

新增：

- `XAUAT.EduApi/Login/Mfa/`（见 5.1）
- `XAUAT.EduApi/Controllers/V1/MfaController.cs`
- `EduApi.Data/Models/MfaChallenge.cs`、`MfaSubmitRequest.cs`、`MfaStatusResponse.cs`、`MfaRequestOptions.cs`
- `XAUAT.EduApi/Exceptions/`（MFA/密码过期相关异常）

修改（核心）：

- `XAUAT.EduApi/Login/Xauat/`：`XauatSsoClient`、`IXauatSsoClient`、`LoginToken`、`XauatCookieJar`
- `XAUAT.EduApi/Login/Unified/`：`UnifiedSsoClient`、`IUnifiedSsoClient`、`UnifiedAuthService`
- `XAUAT.EduApi/Login/`：`SsoAuthService`、`SsoLoginService`、`ILoginRedisStore`、`LoginRedisStore`、`LoginCacheKeys`
- `XAUAT.EduApi/Controllers/V1/`：`LoginController`、`UnifiedAuthController`、`ApiCodes`
- `XAUAT.EduApi/Controllers/Old/LoginController.cs`
- `XAUAT.EduApi/Services/GraduateLoginService.cs`、`Interfaces/ILoginService.cs`、`Interfaces/IGraduateLoginService.cs`
- `XAUAT.EduApi/Extensions/ServiceCollectionExtensions.cs`
- `EduApi.Data/Models/`：`LoginRequest`、`ApiResponseModels`、`UnifiedAuthRequest`、`UnifiedAuthResponse`
- `XAUAT.EduApi/Localization/`：`ApiMessageKey`、`ApiMessageLocalizer`

### 9.3 相关文档

- [详细手动测试教程](MANUAL_TESTING.md)
- [学校通知截图](news.jpg)
