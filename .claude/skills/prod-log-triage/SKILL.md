---
name: prod-log-triage
description: 巡检 XAUAT.EduApi 生产环境（https://xauatapi.xauat.site）最近日志中的 Warning/Error/Fatal，聚类异常并研判高危问题。当用户提到“生产日志”“线上报错”“最近 error/warning”“日志巡检”“高危问题”“服务是否正常”“最近出了什么错”时使用。
allowed-tools: Bash(python3 *), Read, Grep, Glob
---

# 生产日志研判

## 前提

- 生产日志端点：`GET https://xauatapi.xauat.site/Logs`，实现在 [LogsController](../../../XAUAT.EduApi/Controllers/LogsController.cs)。
- `level` 是**最低等级**：`level=Warning` 同时返回 Warning、Error、Fatal。
- 生产配置了 `LOG_VIEW_TOKEN`，请求需带 `X-Log-Token`（或 `Authorization: Bearer`）。
- token 解析顺序：`--token` → `EDUAPI_LOG_TOKEN` → `LOG_VIEW_TOKEN` → 仓库内 `XAUAT.EduApi/.env` 的 `MAP_ADMIN_TOKEN`（已实测生产接受该值）。脚本会在 stderr 打印来源，但**绝不打印值**。
- `MAP_ADMIN_TOKEN` 同时具备 Map 管理端写权限，本 Skill 只用它读 `/Logs`。不要把它写进报告、提交到仓库或粘贴到外部工具；`.claude/settings.local.json` 已被 git 跟踪，不能放 secret。

## 一步拉取

```bash
python3 .claude/skills/prod-log-triage/scripts/fetch_logs.py --hours 24
```

常用变体：

```bash
# 只看 Error/Fatal
python3 .claude/skills/prod-log-triage/scripts/fetch_logs.py --level Error --hours 72

# 按关键词过滤（服务端 search，匹配 message/source/exception）
python3 .claude/skills/prod-log-triage/scripts/fetch_logs.py --hours 24 --search "登录"

# 把完整 JSON 落盘，方便二次分析
python3 .claude/skills/prod-log-triage/scripts/fetch_logs.py --hours 24 --json-out /tmp/prod-logs.json
```

脚本只读、只做拉取和聚类：stdout 是 JSON，stderr 是一行摘要。不会调用登录、封禁等会改变状态的接口。

## 分析流程

1. **先看覆盖范围**，再下结论：
   - `coverage.total_reported` / `coverage.fetched_unique` / `coverage.possibly_truncated`
   - `time_range_cst.earliest` / `.latest`
   - 服务端内存最多 2000 条，重启会丢；触顶说明“最近”可能短于 `--hours`。
   - 即使 `possibly_truncated=false`，若 `time_range_cst` 跨度远小于 `--hours`，通常说明 2000 条容量被 Information 等低级别日志占满、可见范围被压缩；不能据此断言整个 `--hours` 内无错误。
2. **看等级分布与时间线**：`counts_by_level`、`timeline_cst`。突增、持续出现比总量更有意义。
3. **按聚类定位**：`clusters` 已按严重度排序（Fatal → Error → Warning，组内按次数）。对每个 P0/P1 候选：
   - 用 `source`（SourceContext，形如 `XAUAT.EduApi.Services.XxxService`）在仓库定位实现；
   - 用 `exception_type` 和消息关键片段搜索调用点；
   - 判断根因属于上游（教务/CAS/Redis）、配置，还是本仓库代码缺陷。
4. **给结论**：只报有证据的问题；明确区分“新出现”“持续”“偶发噪音”。

## 高危判定

P0（立即处理）：
- 任何 Fatal；
- 认证链路（`XAUAT.EduApi.Login.*`、`Sso*`、`Unified*`、`Graduate*`）反复 Error；
- 启动/迁移失败、数据库连接失败；
- 支付（Payment）或电费订阅等写链路失败；
- 短时间内大量同类 Error（占比高或陡增）。

P1（当天处理）：
- 同一聚类反复出现（例如 ≥ 3 次且跨多个时间点）；
- 窗口早期没有、近期新出现的异常类型；
- 外部上游超时 / TLS / DNS（教务系统、CAS、gmis）；
- Redis/缓存不可用导致降级；
- 后台服务（BackgroundService）循环异常。

P2（观察）：
- 偶发 Warning、重试后成功、健康检查噪音、可降级的上游波动。

## 报告格式（中文）

1. **概览**：时间窗口、条数、等级分布、覆盖是否完整。
2. **高危问题**（P0/P1），每条包含：
   - 现象与证据：次数、首次/最后时间、来源类、典型样本；
   - 代码位置：仓库相对路径 + 行号；
   - 可能根因；
   - 建议动作（诊断命令或修复方向）。
3. **观察项**（P2/噪音）。
4. **数据局限**：内存上限、单实例、UTC→北京时间换算、日志可能含敏感信息。

## 注意

- 日志时间戳是 UTC（容器未设 `TZ`），脚本已换算为 `*_cst`。引用时间用北京时间并注明。
- 多实例部署时 `/Logs` 只反映被命中的那个实例，结论里要写明这一限制。
- 日志可能包含 cookie、学号等敏感信息；脚本已做基础脱敏，报告中仍不要贴原始值。
- 默认只诊断、不改代码；用户明确要求修复后再动手。
