# XAUAT 统一身份认证二次认证（MFA / re-auth）分析与实现报告

- 日期：2026-11-14（初版 2026-11-12）
- 范围：`POST /v1/login`、`POST /v1/login/unified`、研究生登录，以及生产环境 `https://xauatapi.xauat.site`
- 测试账号：已脱敏；密码/OTP 不在本报告中出现
- 结论一句话：**生产 401 的直接原因是账号密码已过期（90 天强制改密），authserver 把登录重定向到「完善资料/改密」页，旧代码把这个 200 页面误判成了「密码错误」；密码修复后，若生产 IP 触发异地 re-auth，还需要 ReAuthHandler 才能继续。**

---

## 1. 结论摘要

1. **学校这次上线的不是「全局登录 MFA」，而是按应用/按风险触发的 step-up re-auth（二次认证）。**
   - 账号 `reAuthEnabled=true`，但强制二次认证的应用只有「财务综合服务平台」和「企微连接器」。
   - 本科教务 `swjw`、研究生 `gmis` 不在强制列表里，所以本机登录这两个系统仍然直接成功、没有 MFA。
2. **re-auth 页面和提交接口已实测打通：**
   - 触发：`302 -> /authserver/reAuthCheck/reAuthLoginView.do?service=...`
   - 提交：`POST /authserver/reAuthCheck/reAuthSubmit.do`
   - 密码方式：`reAuthType=2` + AES 加密后的 `password`
   - OTP（安全令牌）：`reAuthType=10` + 6 位 `otpCode`
   - 信任此设备：`skipTmpReAuth=true`
   - 成功后重新 `GET /authserver/login?service=...`，CAS 会返回带 `ticket=ST-...` 的 302。
3. **生产 401 的直接原因（2026-11-14 最新实测确认）：账号密码已过期（管理员要求 90 天改密）。**
   - 密码本身正确；但 authserver 会把所有 service 登录重定向到 `improveInfo/improveUserInfo.do`。
   - 该页的完成接口返回 `{"errMsg":"当前密码已过期。为了您的账号安全，管理员要求您90天修改一次密码"}`。
   - `XauatSsoClient`/`UnifiedSsoClient` 不认识这个页面，最终统一变成 `LoginFailedException` → 控制器固定返回 401「用户名或密码错误」。
   - 本机早期登录 `swjw`/`gmis` 成功，是因为当时尚未触发过期检查；现在（2026-11-14）本机也会被拦到改密页。
   - **2026-11-14 更新**：用户改密后，用正确的含 `@` 新密码 + 人工验证码登录成功；`swjw`/`gmis` 已恢复直接拿票；生产只需更新密码。
4. **密码修改后仍可能遇到第二个阻塞：生产服务器 IP 触发 `ipAnomaly>0` 的异地 re-auth。**
   - 企微连接器的 re-auth 使用 `reAuthType=10`（OTP），已用 Aegis 6 位码实测通过；财务平台仍用 `reAuthType=2`（密码）。
   - authserver 会 302 到 `reAuthCheck/reAuthLoginView.do`，强制 `reAuthType=2`（再次输入密码）。
   - `XauatSsoClient` 把「302 到 re-auth 页」当成 SSO 票据，或把「200 的 re-auth 页」当成无 `span#msg` 的登录失败 → 同样 401。
   - 本机 IP 在常用地内时 `ipAnomaly=0`，所以这一层在本机复现不出来；生产 IP 才可能触发。
5. **生产现在出现的 429 是应用自身的限流，不是学校返回的：**
   - 实测 `POST /v1/login` 返回 `429, Retry-After: 659`。
   - 来源是 `CasAttemptGuard`（同一认证 host + 学号，5 次 / 20 分钟），说明之前连续 401 已经把额度打满。
   - 等窗口重置后，用修复后的代码验证即可。
6. **修复建议：先改密；再在标准与通用登录链路中加入 `ReAuthHandler`，遇到 re-auth 页时自动用密码完成二次认证；OTP/TOTP 作为可选增强。**
   - 密码 re-auth 是当前账号/财务应用唯一可自动化的路径（`changeReAuthType.do` 返回 `code=0`，不允许切换方式）。
   - 「信任此设备」在 `isSleepAccount=1` 时不会弹窗、也不会建立可信设备；不能把它当作修复手段。

---

## 2. 背景

学校通知：2026-11-12 起统一身份认证平台启用多因子二次认证，三种方式：

1. 企业微信验证码
2. 微信扫码
3. 安全令牌 OTP（通知中提到「数盾 OTP」小程序；用户已确认可用 Aegis 这类标准认证器扫码绑定）

通知描述的是「第一步一次认证（账号密码/扫码）→ 第二步二次认证」。实测表明，第二步在当前账号上**不是每次登录都触发**，而是：

- 访问被配置为「强制二次认证」的应用时触发；
- 或异地登录（`ipAnomaly>0`）时触发；
- 或应用自身的 re-auth 策略触发。

---

## 3. 实测方法与证据

### 3.1 实测环境

- 本机：中国大陆网络；使用 Python 3.14 + requests，按现有 `XauatSsoClient` 的同一套表单字段复刻登录。
- 认证服务器：`http://authserver.xauat.edu.cn/authserver/login`（上游是明文 HTTP，仓库中已注明）。
- 目标系统：`swjw.xauat.edu.cn`、`gmis.xauat.edu.cn`、财务综合服务平台、企微连接器。
- 生产：`https://xauatapi.xauat.site`（部署在境外/云上，出口 IP 与开发者本机不同）。

### 3.2 关键观察

| 测试                                                                   | 结果                                                                                              |
| -------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| 本机登录 `swjw`（正确盐值 + AES 密码）                                           | `302 -> swjw...?ticket=ST-...`，落地 200，拿到 `__pstsid__`/`SESSION`，**无 MFA**                       |
| 本机登录 `gmis`                                                          | `302 -> gmis...?ticket=ST-...`，**无 MFA**                                                        |
| 登录页 `checkNeedCaptcha.htl?username=...`                              | `{"isNeed":false}`，账号当前不需要验证码                                                                   |
| 账号偏好 `POST /personalInfo/accountRelation/userSettingDetail`          | `reAuthEnabled=true`                                                                            |
| 强制二次认证应用                                                             | `财务综合服务平台`（`https://zhfwpt.xauat.edu.cn:8080/logCas`）、`企微连接器`（`https://qiwei.xauat.edu.cn/cas`） |
| 可选二次认证应用                                                             | 空                                                                                               |
| OTP 绑定状态 `POST /personalInfo/otpToken/isBind`                        | `{"code":"0","data":true}`，已绑定                                                                  |
| 可信设备 `GET /personalInfo/accountSecurity/queryAllDevices`             | `[]`                                                                                            |
| 未信任的多因子设备 `POST /personalInfo/common/showMultiFactorUntrustedDevice` | 返回两条 `windows_10 chrome_...`，均为未信任                                                              |
| 生产 `POST /v1/login`                                                  | 用户先前看到 401；本次实测返回 `429, Retry-After: 659`（应用自身限流已满）                                             |
| 改密后本机登录 `swjw` / `gmis` | 直接 `302 ...?ticket=ST-...`，无 MFA |
| 企微连接器 re-auth（`reAuthType=10`） | Aegis 6 位码 → `{"msg":"认证成功","code":"reAuth_success"}` → `ticket=ST-...` |
| 企微连接器 re-auth（`reAuthType=3` 短信） | 发送 `{"res":"success","codeTime":120}` → 提交 `dynamicCode` → `reAuth_success` → `ticket=ST-...` |
| 企微连接器 re-auth（`reAuthType=8` 微信扫码） | 扫码确认 → `wx_errcode=405` → callback → `ticket=ST-...` |

### 3.3 触发 re-auth 的完整链路（本机实测）

```text
1. GET  /authserver/login                       → 200 登录页（pwdEncryptSalt、execution、lt）
2. POST /authserver/login                       → 302 /authserver/index.do（拿到 CASTGC）
3. GET  /authserver/login?service=<受保护应用>  → 302 /authserver/reAuthCheck/reAuthLoginView.do?service=<受保护应用>
4. GET  /authserver/reAuthCheck/reAuthLoginView.do?service=...
                                                → 200 re-auth 页面，内联 var reAuthParams = {...}
5. POST /authserver/reAuthCheck/reAuthSubmit.do → {"msg":"认证成功","code":"reAuth_success"}
6. GET  /authserver/login?service=<受保护应用>  → 302 <应用>?ticket=ST-...
```

第 5 步使用密码方式时，请求体（form-urlencoded）核心字段：

```text
service=<受保护应用>
reAuthType=2
isMultifactor=<页面值，通常为空>
password=<AES(password, reAuthParams.pwdEncryptSalt)>
dynamicCode=
uuid=
answer1=
answer2=
otpCode=
skipTmpReAuth=true|false
```

第 5 步使用 OTP 方式时：

```text
reAuthType=10
otpCode=<6 位数字>
```

失败响应为 `{"code":"reAuth_failed","msg":"..."}` 或 `{"code":"reAuth_unauthorized","msg":"..."}`。

---

## 4. 学校 re-auth 机制拆解

### 4.1 `reAuthParams` 字段

页面内联：

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
  otpCodeRequired: "请输入6位有效的安全令牌",
  ...
};
```

关键字段含义：

| 字段               | 含义                               |
| ---------------- | -------------------------------- |
| `service`        | 当前要访问的受保护应用地址，提交 re-auth 时必须原样带回 |
| `reAuthType`     | 当前二次认证方式；`2`=密码，`10`=安全令牌 OTP    |
| `pwdEncryptSalt` | re-auth 页自己的密码盐值，和登录页的盐不是同一个     |
| `isMultifactor`  | 是否多因子场景，通常为 `null`/空             |
| `isSleepAccount` | 页面是否展示「信任此设备」弹窗；实测 `1` 时不展示      |
| `ipAnomaly`      | 异地登录标记；`>0` 时强制走密码二次认证           |

### 4.2 `reAuthType` 取值表（页面 i18n 文案反推）

| 值      | 方式                |
| ------ | ----------------- |
| 2      | 再次输入密码            |
| 3      | 短信验证码             |
| 4      | 企业号验证码            |
| 5      | 今日校园验证码           |
| 6 / 18 | 实人认证（支付宝/腾讯云人脸）   |
| 7      | 安全问题              |
| 8      | 微信扫码              |
| 9      | QQ 扫码             |
| **10** | **安全令牌 OTP（6 位）** |
| 11     | 邮箱验证码             |
| 12     | 钉钉验证码             |
| 13     | WeLink 验证码        |
| 14     | 钉钉/企业微信/WeLink 扫码 |

### 4.3 「信任此设备」

- 页面有「是否信任此登录设备？」弹窗，文案：若信任，下次在此设备登录时无需多因子校验。
- 前端逻辑：只有 `isSleepAccount === '0'` 才展示弹窗；点击「信任此设备」后把 `skipTmpReAuth=true` 带到 `reAuthSubmit.do`；「仅本次登录」则为 `false`。
- 实测：当前账号 `isSleepAccount=1`，弹窗不出现；即使手动传 `skipTmpReAuth=true`，`queryAllDevices` 仍为空，新会话登录仍触发 re-auth。
- `MULTIFACTOR_BROWSER_FINGERPRINT` cookie 由 `GET /authserver/bfp/info?bfp=...` 设置（Max-Age≈2094 年），但它本身不能免 re-auth。
- 结论：**不要依赖信任设备来修复生产 401；每次自动完成密码 re-auth 才是稳定方案。**

### 4.4 OTP 现状

- 账号已绑定 OTP（`otpToken/isBind = true`）。
- re-auth 页支持 `reAuthType=10`，提示「请输入 6 位有效的安全令牌」。
- 财务应用页面固定 `reAuthType=2`；调用 `POST /authserver/reAuthCheck/changeReAuthType.do` 切换 `10/4/2` 均返回 `{"code":"0","data":null}`（前端以 `code==1` 为成功），说明当前会话/应用**不允许切换**到 OTP。
- **2026-11-14 实测成功**：企微连接器的 re-auth 页 `reAuthType=10`、`_isBindOtp=true`；用 Aegis 当前 6 位码提交 `POST /authserver/reAuthCheck/reAuthSubmit.do`（`service=https://qiwei.xauat.edu.cn/cas`、`reAuthType=10`、`otpCode=<6 位>`、`skipTmpReAuth=false`）返回 `{"msg":"认证成功","code":"reAuth_success"}`，随后 `GET /authserver/login?service=...` 返回 `302 ...?ticket=ST-...`。
- 结论：**OTP 是标准 6 位 TOTP 兼容的路径，已用真实码验证通过**；财务应用仍固定密码，企微连接器走 OTP。
- **企业号验证码（`reAuthType=4`）现状（2026-11-14 实测）**：
  - 发送接口 `POST /authserver/dynamicCode/getDynamicCodeByReauth.do`，`authCodeTypeName=reAuthWChatDynamicCodeType`，返回 `{"res":"wechat_success","returnMessage":"验证码已发送成功"}`，说明企业微信通道可用。
  - 但 `changeReAuthType.do` 切 `reAuthType=4` 返回 `{"code":"0","data":null}`；在同一会话里用收到的企业微信码提交 `reAuthSubmit.do`（`reAuthType=4`、`dynamicCode=<码>`）仍返回 `{"msg":"认证失败","code":"reAuth_failed"}`。
  - 结论：**企微连接器应用不接受 `reAuthType=4`**；要测试企业号验证码，需要走门户的登录时 MFA（通知里的三种方式），或在个人中心把默认二次认证方式改成 4（应用策略可能仍覆盖）。
  - 同一页面允许 `reAuthType=3`（短信）：`changeReAuthType.do` 返回 `{"code":"1","data":{"reAuthType":"3",...}}`，可作为动态码提交路径的替代测试。
  - `refreshReAuthType.do` 在 `10`（OTP）和 `3`（短信）之间循环，未出现 `4`，进一步确认该应用只提供这两种动态方式。
  - 进一步用 `isMultifactor=true`/`1` 强制请求：`changeReAuthType(4)` 仍返回 `code:0`；`refreshReAuthType` 财务返回空、企微返回 `10`，均未出现 4。
  - 当前账号的门户登录不触发 MFA，偏好设置也没有 MFA 选项——因此**当前账号无法到达提供 `reAuthType=4` 的多因子上下文**；这是服务端策略，客户端无法强制。要测试 4，只能等学校给该账号/应用启用多因子策略，或提供门户 MFA 的 HAR。
  - **`reAuthType=3`（短信）已实测通过**：`changeReAuthType.do` 返回 `code:1`；`getDynamicCodeByReauth.do`（`authCodeTypeName=reAuthDynamicCodeType`）返回 `{"res":"success","mobile":"155****9955","codeTime":120}`；同一会话提交 `reAuthSubmit.do`（`reAuthType=3`、`dynamicCode=<短信码>`）返回 `{"msg":"认证成功","code":"reAuth_success"}`，随后 `login?service=...` 返回 `ticket=ST-...`。
  - 因此 `dynamicCode` 提交分支已验证；type 4 与 type 3 的提交结构相同，只是发送时 `authCodeTypeName=reAuthWChatDynamicCodeType`、通道为企业微信。当前账号的应用策略不提供 4，无法端到端实测 4，但协议实现可以按同一套写。
- **微信扫码（`reAuthType=8`）现状（2026-11-14 实测成功）**：
  - `changeReAuthType.do` 切 `8` 返回 `{"code":"1","data":{"reAuthTypeName":"微信扫码","reAuthType":"8",...}}`。
  - `GET /authserver/combinedLogin.do?type=weixin&reAuth=1&success=<service>` → `302 https://open.weixin.qq.com/connect/qrconnect?appid=...&redirect_uri=https://authserver.xauat.edu.cn/authserver/callback&scope=snsapi_login&state=...`。
  - 扫码页返回二维码 UUID；二维码图片：`https://open.weixin.qq.com/connect/qrcode/<uuid>`；状态轮询：`https://lp.open.weixin.qq.com/connect/l/qrconnect?uuid=<uuid>`，`wx_errcode=405` 时返回 `wx_code`。
  - 用同一 authserver 会话跟随 `callback?code=<wx_code>&state=<state>` → `302 login?service=<service>` → `302 <service>?ticket=ST-...`。
  - 结论：**微信扫码路径已端到端验证**；它是用户扫码 + 轮询 + 回调的多步流程，不是 `reAuthSubmit.do` 表单提交。

### 4.5 密码过期（2026-11-14 最新发现，优先级最高）

- 触发条件：管理员策略要求每 90 天修改一次密码；当前账号的密码已过期。
- 表现：所有 service（`swjw`、`gmis`、财务综合服务平台、企微连接器）登录都会：
  `302 -> /authserver/improveInfo/improveUserInfo.do?service=...`，页面标题「完善资料」。
- 页面完成接口 `POST /authserver/improveInfo/updateUserInfo.do` 返回：
  ```json
  {"errMsg":"当前密码已过期。为了您的账号安全，管理员要求您90天修改一次密码"}
  ```
- 改密接口（页面「修改密码」弹窗实际调用）：
  - `POST /authserver/improveInfo/improveInfoUpdatePassword.do`
  - 参数：`oldPassword`、`newPassword`、`confirmPassword`，三者均用 `pwdDefaultEncryptSalt = "q1hVinmcu2i7fPyf"` 做 AES 加密。
  - 另有两步式 `POST /authserver/improveInfo/updatePassword.do`（带 `step`/`sign`），是另一套改密 UI。
- 结论：**必须先改密**，否则 MFA/re-auth 根本走不到。旧代码不识别这个页面，统一报 401「密码错误」，与真实原因完全不符。
- 2026-11-14 实测（更新）：用户最终确认新密码为含 `@` 的版本；先用错误密码尝试导致 `checkNeedCaptcha` 变为 `true`，随后用人工识别的验证码登录成功，验证码状态清除。
  - 登录成功后 `swjw` / `gmis` 直接返回 `ticket=ST-...`，不再出现 `improveInfo` 页。
  - 财务平台 `reAuthType=2`（密码）；企微连接器 `reAuthType=10`（OTP，`_isBindOtp=true`）。
  - **生产 API 只需把密码更新为这个新密码，`/v1/login` 即可恢复；如果生产 IP 仍触发异地 re-auth，再由 ReAuthHandler 处理。**

---

## 5. 现有代码为什么失败

### 5.1 标准 `/v1/login`

调用链：

```text
LoginController.Login
  → SsoLoginService.LoginAsync
    → SsoAuthService.LoginAsync
      → XauatSsoClient.LoginAsync
        → GET 登录页
        → POST 凭据（不跟随重定向）
        → InterpretLoginResponseAsync
      → XauatSsoClient.ExchangeSsoTicketAsync（换教务会话）
```

问题点：

1. `XauatSsoClient.LoginAsync`（`XAUAT.EduApi/Login/Xauat/XauatSsoClient.cs:29-96`）只取登录页 `lt/execution/pwdEncryptSalt`，POST 后交给 `InterpretLoginResponseAsync`。
2. `InterpretLoginResponseAsync`（同文件 `:98-140`）：
   - 只要响应是 301/302，就读取该响应的 `Set-Cookie` 当 SSO 票据；**它不检查 302 的 `Location` 是不是 re-auth 页**。
   - re-auth 的 302 可能带一个会话 cookie，于是被误当作有效 SSO 票据；后续换票失败或拿到空 eduCookie → 401。
   - 如果 POST 直接返回 200 的 re-auth 页，且页面没有 `span#msg`，则返回「登录失败：用户名或密码错误」→ 401。
3. `ExchangeSsoTicketAsync`（同文件 `:150-198`）用 `SendFollowingRedirectsAsync` 跟跳转；如果最后一跳是 re-auth 页（200），它只检查 `final.StatusCode == 200`，然后从 `history[1]` 取 cookie；re-auth 未完成，取到的不是教务会话 → 空/无效 cookie → 401。
4. `SsoAuthService`（`XAUAT.EduApi/Login/SsoAuthService.cs:145-202`）把 `LoginToken.Failed` 统一转成 `SsoAuthResult.Fail`；`SsoLoginService` 再抛 `LoginFailedException`；`LoginController`（`Controllers/V1/LoginController.cs:50-54`）固定返回 401 +「用户名或密码错误」。
   - 所以**上游到底说了什么，调用方永远看不到**。

### 5.2 通用 `/v1/login/unified`

调用链：

```text
UnifiedAuthService.LoginAsync
  → UnifiedSsoClient.LoginAsync
    → DiscoverLoginPageAsync
    → SubmitCredentialsAsync
    → FollowBackAsync
```

问题点：

1. `SubmitCredentialsAsync`（`XAUAT.EduApi/Login/Unified/UnifiedSsoClient.cs:302-333`）：
   - 302 时直接 `FollowBackAsync`，没有识别 `Location` 是否是 `reAuthCheck`。
   - 200 时只检查 `LooksLoggedIn(html)`（正文含「退出」/`logout`），否则一律 `InvalidCredentials`。re-auth 页没有这些关键字 → 失败。
2. `FollowBackAsync`（同文件 `:338-382`）：
   - 某一跳如果是 re-auth 页（200），会继续当成「落地页」；`LooksLikeLoginPage(html)` 只看 `pwdEncryptSalt`/`execution`/`lt`/`username+password`，re-auth 页虽然有 `pwdEncryptSalt`，但结构不同，可能不命中；最终可能返回成功但 cookie 是 re-auth 会话，或把 401/403 判为凭据错误。
3. `UnifiedAuthFailure`（`IUnifiedSsoClient.cs:8-24`）没有 `MfaRequired`/`ReAuthRequired` 分类，所有非成功都落到 `InvalidCredentials` 或 `Upstream`。
4. 研究生登录 `GraduateLoginService` 复用 `IUnifiedAuthService`，同样受影响。

### 5.3 生产 401 的判定（按优先级）

**情况 A（当前已确认）：密码过期。**

```text
生产服务器 → POST /authserver/login（密码正确但已过期）
  → 302 /authserver/improveInfo/improveUserInfo.do?service=...
  → GET 页面 200，标题「完善资料」
  → XauatSsoClient 不认识该页面，ParseMessageSpan 取不到 #msg
  → "登录失败：用户名或密码错误"
  → 控制器 401 "用户名或密码错误"
```

**情况 B（改密后可能遇到）：异地 re-auth。**

```text
生产服务器（境外 IP）→ GET /authserver/login?service=swjw...
  → authserver 判定 ipAnomaly>0
  → 302 /authserver/reAuthCheck/reAuthLoginView.do?service=swjw...
  → XauatSsoClient 把 302 当 SSO 票据 / 把 200 re-auth 页当密码错误
  → LoginFailedException
  → 控制器 401 "用户名或密码错误"
```

本机早期因为 IP 在常用地内且密码尚未过期，同样的代码却能成功——这解释了「本地能登录、服务器不能登录」的差异。现在（2026-11-14）本机密码也已过期，因此本机同样被拦到改密页。

> 说明：生产日志需要 `LOG_VIEW_TOKEN` / `MAP_ADMIN_TOKEN` 才能拉取；当前本地仓库没有 `.env`。密码过期已由本地实测确认；`ipAnomaly` 仍是高置信度推断，最终以生产日志中的 `用户 ... 登录失败: {Message}` 为准。

---

## 6. 修复方案

### 6.1 新增 `ReAuthHandler`

建议位置：`XAUAT.EduApi/Login/Unified/ReAuthHandler.cs`（`internal`）。

职责：

- `LooksLikeReAuthPage(html/url)`：识别 `reAuthLoginView.do` 或 `var reAuthParams`。
- `Parse(html)`：正则提取 `var reAuthParams = {...};`，`JsonSerializer.Deserialize`。
- `CompleteAsync(...)`：
  - 选择方式：
    - 调用方提供 `OtpCode` → `reAuthType=10` + `otpCode`
    - 调用方提供 `OtpSecret` → 本地生成 TOTP → `reAuthType=10` + `otpCode`
    - 调用方提供 `MfaCode` 且页面类型 ∈ {3,4,5,11,12,13} → 对应类型 + `dynamicCode`
    - 否则 → `reAuthType=2` + `password=AES(password, page.pwdEncryptSalt)`
  - POST `reAuthSubmit.do`，带 `service`、`isMultifactor`、`skipTmpReAuth=<TrustDevice>`。
  - 解析 `reAuth_success` / `reAuth_failed` / `reAuth_unauthorized`。
- 成功后由调用方再次 `GET login?service=...` 拿 ticket，或由 handler 直接跟随后返回。

### 6.2 接入标准 `/v1/login`

- `IXauatSsoClient.LoginAsync` 增加 `ReAuthOptions?` 参数，`SsoAuthService`/`SsoLoginService`/`ILoginService` 逐层透传。
- `InterpretLoginResponseAsync`：
  - 302 的 `Location` 含 `reAuthLoginView` → 取 re-auth 页 → `ReAuthHandler.CompleteAsync` → 成功后 `GET AuthServerUrl?service=...` 继续 `FollowAsync`。
  - 200 且 `LooksLikeReAuthPage` → 同样处理。
- `ExchangeSsoTicketAsync`：最后一跳是 re-auth 页时，完成 re-auth 后重试换票。
- **不要**在 re-auth 提交时再次调用 `CasAttemptGuard.AcquireAsync`：同一次登录只占一个限流名额。

### 6.3 接入通用 `/v1/login/unified`

- `UnifiedAuthCommand` 增加 `ReAuthOptions`；`ReadSessionAsync`/缓存键不需要包含 OTP（OTP 只在本次请求使用）。
- `SubmitCredentialsAsync`：POST 响应是 re-auth 页（200 或 302 到 `reAuthCheck`）时调用 handler，成功后从 `login?service=...` 继续。
- `FollowBackAsync`：每一跳先判断 re-auth 页；处理成功后继续回走。
- 票据复用路径（CASTGC 换票）经过 `FollowBackAsync`，自动复用同一处理。
- `GraduateLoginService` 通过 `IUnifiedAuthService` 自动获得支持。

### 6.4 请求字段

`LoginRequest` / `UnifiedAuthRequest` 增加可选字段：

| 字段            | 说明                                           |
| ------------- | -------------------------------------------- |
| `OtpCode`     | 6 位安全令牌，交互式传入                                |
| `OtpSecret`   | `otpauth://` URI 或 base32；**仅内存使用，不落库、不落日志** |
| `MfaCode`     | 企业微信/短信/邮箱等动态码                               |
| `TrustDevice` | 默认 `true`，对应 `skipTmpReAuth=true`            |

`OtpSecret` 等同长期凭据，**不要**存 Redis/DB；如果客户端要自动化，由客户端每次传入。

### 6.5 OTP / TOTP（可选增强）

新增 `XAUAT.EduApi/Login/Totp.cs`：

- `TryParseOtpauth(uri)`：解析 `otpauth://totp/...?secret=...&digits=6&period=30`。
- `Generate(base32Secret, now, digits=6, period=30)`：RFC 6238 / HMAC-SHA1。
- 仅在调用方提供 `OtpSecret` 时使用；失败回退密码 re-auth（若页面允许）。

### 6.6 错误映射

- 页面强制 OTP（`reAuthType=10` 且不允许切换）且调用方未提供码时：
  - 返回新 `ApiCodes.MfaRequired = 20003` + `ApiMessageKey.MfaCodeRequired`（八语言），不要伪装成密码错误。
- `reAuth_failed` / `reAuth_unauthorized` → `LoginFailedException`（401），日志保留上游 `msg`。
- 解析不到 re-auth 页/上游返回异常 → 502，不要静默当密码错误。

### 6.7 限流

- 生产 429 来自 `CasAttemptGuard`（5 次/20 分钟，key = authserver host + username）。
- 修复后每次登录只应消耗 1 次额度；re-auth 不额外消耗。
- 验证生产前先等 `Retry-After`（本次约 11 分钟）重置；不要循环重试。
- 如果生产上多客户端共用同一账号，仍需评估 5 次/20 分钟是否足够（当前设计是每账号独立计数）。

### 6.8 安全

- 密码、OTP、`OtpSecret` 只在内存与表单中出现；不写日志、不写缓存、不进 URL。
- re-auth 页面同样可能被上游替换；解析失败按上游错误（502）处理。
- `TrustDevice=true` 会尝试信任设备，服务端可能忽略（`isSleepAccount=1`）；不要把它当安全边界。
- 本次分析中密码/凭据已在对话中出现，验证完成后建议修改一次密码。

### 6.9 运维临时方案（可选）

如果暂时不改代码，可以：

- 让生产服务器走**中国大陆出口 IP**（代理/专线），避免 `ipAnomaly>0`；或
- 让生产服务器 IP 进入学校「常用地」/白名单（通常不可行）；
- 但这只是绕过，不能替代代码修复。

---

## 7. 测试与验证

### 7.1 单元测试

新增 `Tests/Login/Unified/ReAuthHandlerTests.cs`：

- 解析 re-auth 页（`reAuthParams`、`pwdEncryptSalt`、`service`）。
- 密码 re-auth：断言 `reAuthType=2`、密码已加密、`skipTmpReAuth=true`。
- OTP re-auth：断言 `reAuthType=10` + `otpCode`。
- `reAuth_failed` / `reAuth_unauthorized` 分支。
- re-auth 不重复消耗 `CasAttemptGuard`。

扩展：

- `UnifiedSsoClientTests`：新增「302 → reAuth 页 → submit → login → callback」脚本，断言最终 `LandingCookie`。
- `SsoAuthServiceTests` / `XauatSsoClientTests`：标准登录遇到 re-auth 页时自动完成，返回非空 eduCookie。
- `TotpTests`：RFC 6238 测试向量 + `otpauth://` 解析。
- 控制器测试：新字段透传；强制 OTP 未提供码时返回 20003。

### 7.2 本地端到端（已手工验证）

```text
GET  /authserver/login?service=https://zhfwpt.xauat.edu.cn:8080/logCas
  → 302 /authserver/reAuthCheck/reAuthLoginView.do?service=...
POST /authserver/reAuthCheck/reAuthSubmit.do
  service=..., reAuthType=2, password=<AES>, skipTmpReAuth=true
  → {"msg":"认证成功","code":"reAuth_success"}
GET  /authserver/login?service=...
  → 302 https://zhfwpt.xauat.edu.cn:8080/logCas?ticket=ST-...
```

### 7.3 生产验证

1. 等 `Retry-After` 重置（本次约 11 分钟）。
2. `POST https://xauatapi.xauat.site/v1/login`，body `{"username":"...","password":"..."}`。
3. 期望 200 + cookie。
4. 若仍 401：
   - 用 `LOG_VIEW_TOKEN`/`MAP_ADMIN_TOKEN` 调 `GET /Logs?level=Warning&search=登录失败`；
   - 看 `用户 ... 登录失败: {Message}` 的真实内容；
   - 若 Message 是「用户名或密码错误」，说明 re-auth 处理未命中；若是「未能获取有效会话」，说明 302 的 Location 判断有遗漏。
5. `/v1/login/unified` 用 `url=https://zhfwpt.xauat.edu.cn:8080/logCas` 验证自动 re-auth。
6. OTP：已实测通过（企微连接器 `reAuthType=10`，Aegis 6 位码 → `reAuth_success` → `ticket=ST-...`）；实现时按 `reAuthType=10` 提交 `otpCode` 即可。

---

## 8. 风险与未知

| 项             | 状态 / 说明                                                           |
| ------------- | ----------------------------------------------------------------- |
| 生产 401 的直接原因     | **已确认：密码过期（90 天策略）**；本地 `updateUserInfo.do` 返回明确 errMsg。改密后可能还有 ipAnomaly → re-auth。 |
| 生产 401 的日志实锤     | 未取得；本地无 `LOG_VIEW_TOKEN`。ipAnomaly → re-auth 仍是高置信度推断。                                  |
| OTP 是否标准 TOTP | **已确认**：企微连接器 `reAuthType=10`，Aegis 6 位码提交返回 `reAuth_success` 并拿到 ticket。 |
| 企业号验证码（type 4） | 发送通道 `wechat_success`，但当前应用策略拒绝 4（change/refresh/提交均失败）；无法端到端实测，需学校启用或门户 MFA HAR。type 3 已验证同样的 `dynamicCode` 提交分支。 |
| 微信扫码（type 8） | **已确认**：`changeReAuthType(8)` code 1；`combinedLogin.do` → 微信二维码 → `lp.open.weixin.qq.com` 轮询 405 → callback → ticket。用户扫码多步流程。 |
| 信任设备          | 当前账号 `isSleepAccount=1` 时不生效；不同应用/不同账号可能不同，不能作为主修复。               |
| re-auth 页面结构  | 页面由 `reAuth.js` 驱动，字段/JS 可能随学校升级变化；解析失败要按上游错误处理。                  |
| 限流            | 生产 429 是应用自身 `CasAttemptGuard`；修复后每次登录只消耗 1 次，但仍需关注 5 次/20 分钟的上限。 |
| 安全            | 密码/OTP/`OtpSecret` 不得落库/落日志；`OtpSecret` 等同长期凭据。                   |

---

## 9. 附录

### 9.1 关键端点

| 端点                                                       | 方法       | 用途                                          |
| -------------------------------------------------------- | -------- | ------------------------------------------- |
| `/authserver/login`                                      | GET/POST | 一次认证（账号密码）                                  |
| `/authserver/reAuthCheck/reAuthLoginView.do?service=...` | GET      | 二次认证页面                                      |
| `/authserver/reAuthCheck/reAuthSubmit.do`                | POST     | 提交二次认证                                      |
| `/authserver/reAuthCheck/changeReAuthType.do`            | POST     | 切换二次认证方式（当前返回 code=0）                       |
| `/authserver/improveInfo/improveUserInfo.do?service=...` | GET      | 密码过期/完善资料页                                  |
| `/authserver/improveInfo/updateUserInfo.do`              | POST     | 「我已完成」；密码过期时返回明确 errMsg                     |
| `/authserver/improveInfo/improveInfoUpdatePassword.do`   | POST     | 改密（old/new/confirmPassword，AES salt `q1hVinmcu2i7fPyf`） |
| `/authserver/bfp/info?bfp=...`                           | GET      | 设置 `MULTIFACTOR_BROWSER_FINGERPRINT` cookie |
| `/personalInfo/accountRelation/userSettingDetail`        | POST     | 读取账号 re-auth 策略                             |
| `/personalInfo/otpToken/isBind`                          | POST     | 查询 OTP 绑定状态                                 |
| `/personalInfo/accountSecurity/queryAllDevices`          | GET      | 可信设备列表（生物识别/设备）                             |
| `/personalInfo/common/showMultiFactorUntrustedDevice`    | POST     | 未信任的多因子设备                                   |
| `/personalInfo/common/recheckApproach`                   | POST     | 当前敏感操作的校验方式                                 |

### 9.2 代码位置

| 文件                                                                      | 关注点                                                                                                          |
| ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `XAUAT.EduApi/Login/Xauat/XauatSsoClient.cs`                            | `LoginAsync:29-96`、`InterpretLoginResponseAsync:98-140`、`ExchangeSsoTicketAsync:150-198`                     |
| `XAUAT.EduApi/Login/Unified/UnifiedSsoClient.cs`                        | `LoginCoreAsync:104-202`、`SubmitCredentialsAsync:302-333`、`FollowBackAsync:338-382`、`BuildLoginForm:394-418` |
| `XAUAT.EduApi/Login/Unified/IUnifiedSsoClient.cs`                       | `UnifiedAuthFailure:8-24`、`UnifiedAuthOutcome:36-47`                                                         |
| `XAUAT.EduApi/Login/SsoAuthService.cs`                                  | `LoginAsync:145-202`                                                                                         |
| `XAUAT.EduApi/Login/SsoLoginService.cs`                                 | `LoginAsync:40-83`                                                                                           |
| `XAUAT.EduApi/Controllers/V1/LoginController.cs`                        | 401/429/502 映射 `:50-76`                                                                                      |
| `XAUAT.EduApi/Controllers/V1/UnifiedAuthController.cs`                  | 400/401/429/502 映射                                                                                           |
| `XAUAT.EduApi/Login/Xauat/XauatHttpSession.cs`                          | 不跟随/手工跟随即捕获 cookie                                                                                           |
| `XAUAT.EduApi/Login/Xauat/XauatCookieJar.cs`                            | `Capture` / `ReadResponseCookies` / `Seed`                                                                   |
| `XAUAT.EduApi/Login/CasAttemptGuard.cs`                                 | 5 次/20 分钟限流                                                                                                  |
| `EduApi.Data/Models/LoginRequest.cs` / `UnifiedAuthRequest.cs`          | 需新增 OTP/MFA 字段                                                                                               |
| `XAUAT.EduApi/Localization/ApiMessageKey.cs` / `ApiMessageLocalizer.cs` | 新增 MFA 文案                                                                                                    |

### 9.3 复现脚本要点（不含凭据）

1. GET 登录页，按 `id=pwdEncryptSalt` 取盐（不是 `name`；页面有多个同名 input）。
2. 用 AES-CBC/PKCS7：key=盐 UTF-8 字节，IV=16 位随机字符，明文=64 位随机前缀+密码，Base64。
3. POST 登录表单：`username`、`password`、`lt`、`execution`、`_eventId=submit`、`captcha=""`、`cllt=userNameLogin`、`dllt=generalLogin`、`rememberMe=true`。
4. 带 `CASTGC` 访问 `login?service=<受保护应用>`；若 302 到 `reAuthLoginView.do`，GET 页面取出 `reAuthParams`。
5. POST `reAuthSubmit.do`：`reAuthType=2` + 加密密码 + `skipTmpReAuth=true`；成功后 `login?service=...` 拿 `ticket=ST-...`。
6. 若页面 `reAuthType=10`（企微连接器），提交 `otpCode=<6 位>`；成功后同样 `login?service=...` 拿票。

---

## 10. 最终建议

1. **更新生产密码（立即）**：密码已改并验证成功；把生产 API/客户端里的密码更新为新密码，`/v1/login` 的密码过期 401 应消失。
2. **落地 ReAuthHandler（按应用分流）**：`reAuthType=2`（财务平台）自动用密码完成；`reAuthType=10`（企微连接器）要求调用方传 `otpCode`，或传 `otpSecret` 由服务端生成 TOTP。
3. **OTP 已实测可用**：Aegis 6 位码 → `reAuth_success` → `ticket=ST-...`；可以直接实现 `otpCode` 字段，`otpSecret` 作为可选增强。
   - 注：企微连接器 app 目前拒绝 `reAuthType=4`（企业号验证码），只接受 3（短信）和 10（OTP）；type 3 已实测通过，type 4 发送通道可用但当前账号无法端到端验证。
   - 注：微信扫码（type 8）已实测通过，走 `combinedLogin.do` → 二维码 → `lp.open.weixin.qq.com` 轮询 → `callback` → ticket；需要多步会话/状态存储。
4. **不要依赖信任设备**：当前 `isSleepAccount=1` 不生效；把它作为「尝试优化」，不作为正确性前提。
5. **补生产日志验证**：拿到 `LOG_VIEW_TOKEN` 后确认 `登录失败:` 的真实 message；如果与推断不符，再针对性调整。
6. **限流与重试**：修复后不要在失败时自动重试登录；re-auth 失败应计入失败次数，避免把账号打进封禁/限流。
