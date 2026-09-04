# XAUAT EduApi

西安建筑科技大学教务系统 API 接口项目，提供对学校教务系统的数据访问接口。

## 项目简介

这是一个基于 ASP.NET Core 的 Web API 项目，旨在为西安建筑科技大学（XAUAT）的教务系统提供统一的数据访问接口。该项目通过模拟登录和数据抓取，为移动应用和其他客户端提供标准化的 RESTful API 服务。

## 功能特性

本项目提供了以下主要功能模块：

### 教务相关
- **登录认证** - 用户登录验证
- **课程信息** - 获取学生课程表
- **成绩查询** - 查询各学期考试成绩
- **考试安排** - 获取考试时间安排
- **培养方案** - 查询专业培养计划
- **学业进度** - 查看学业完成情况

### 校园服务
- **校车时刻** - 查询校车运行时间表
- **一卡通消费** - 查询校园卡消费记录

### 应用更新
- **版本检测** - 检查移动端应用最新版本

## 技术架构

- 基于 .NET 9.0 构建
- 使用 Entity Framework Core 进行数据持久化
- 支持 SQLite（开发环境）和 PostgreSQL（生产环境）
- 集成 Redis 缓存提高性能
- 支持 Docker 容器化部署
- 使用 CORS 解决跨域问题

## 环境配置

项目启动时会自动使用 `DotNetEnv` 加载根目录下的 `.env` 文件，不再依赖 `appsettings.json`。

```bash
cp .env.example .env
```

| 变量名 | 说明 | 示例 |
|-------|------|-----|
| SQL | 数据库连接字符串（PostgreSQL） | `Host=localhost;Database=xauat_edu` |
| REDIS | Redis 连接字符串 | `localhost:6379` |
| SERVICE_NAME | 服务名称 | `XAUAT.EduApi` |
| ELECTRICITY_SUBSCRIPTION_SCAN_INTERVAL_MINUTES | 电费订阅扫描间隔（分钟） | `15` |
| SMTP_HOST | SMTP 服务器地址 | `smtp.qq.com` |
| TEST_ACCOUNT_ENABLED | 是否启用测试账号 | `false` |
| LOG_VIEW_TOKEN | 日志接口访问令牌（设置后启用鉴权） | `change-me` |

## 部署方式

### Docker 部署（推荐）

```bash
docker build -t xauat-edu-api .
docker run -d -p 8080:8080 xauat-edu-api
```

### 本地运行

```bash
cp .env.example .env
dotnet run --project XAUAT.EduApi
```

## API 接口文档

项目集成了 Scalar API 文档，启动后可通过 `/scalar/v1` 路径访问详细的接口文档。

### 日志查询

`GET /Logs?page=1&pageSize=50` 返回日志文件及当前运行日志，按时间倒序分页。可通过 `level`（最低级别）和 `search`（关键词）筛选。服务启动时会读取 `logs/log-*.txt`，运行期间继续写入按天轮转的日志文件，并保留最近 2000 条内存记录；配置 `LOG_VIEW_TOKEN` 后，请使用 `X-Log-Token` 请求头或 Bearer Token 访问。

## MCP 集成

本项目内置 [Model Context Protocol (MCP)](https://modelcontextprotocol.io) 支持，允许 AI 客户端（如 Claude Desktop、Cursor）直接调用教务数据接口。

### 可用 Tools

| Tool | 说明 |
|------|------|
| `GetCourse` | 获取学生课程表，需提供学号和 Cookie |
| `GetScores` | 获取指定学期成绩，需提供学号、学期和 Cookie |
| `GetCurrentSemester` | 获取当前学期信息 |
| `GetExamArrangements` | 获取考试安排，需提供 Cookie |
| `GetBus` | 查询校车时刻表，无需认证 |
| `GetElectricityBalance` | 查询电费余额和本周用电量 |

> 需要认证的接口需传入教务系统 Cookie（登录后从 `/Login` 接口获取，值为 `__pstsid__=...` 格式，或通过 `xauat` 请求头传入）。

### 网络访问（HTTP Transport）

服务启动后 MCP endpoint 位于 `/mcp`，在客户端配置文件中添加：

```json
{
  "mcpServers": {
    "xauat-edu": {
      "url": "http://localhost:5000/mcp"
    }
  }
}
```

**Claude Desktop** 配置文件路径：
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
- Windows: `%APPDATA%\Claude\claude_desktop_config.json`

### 本地访问（Stdio Transport）

如需通过 stdio 模式在本地使用（适合 Claude Code CLI 等工具），可使用 `curl` 或直接指向本地地址：

```json
{
  "mcpServers": {
    "xauat-edu": {
      "url": "http://127.0.0.1:5000/mcp"
    }
  }
}
```

## 主要依赖

- ASP.NET Core 9.0
- Entity Framework Core
- PostgreSQL / SQLite
- Redis
- HttpClientFactory
- Scalar API 文档工具

## 注意事项

1. 本项目仅用于学习和技术研究目的
2. 使用时需要遵守学校相关规定，不得用于非法用途
3. 开发者不对因使用本项目造成的任何后果负责

## 许可证

本项目采用 MIT 许可证，详情请查看 [LICENSE](LICENSE) 文件。
