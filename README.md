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

### GitHub Actions 自动部署

仓库包含生产工作流 `.github/workflows/deploy-production.yml`：提交到 `master` 后会先执行测试，再构建并推送 GHCR 镜像，最后通过 SSH 让服务器拉取该次提交对应的镜像并重启容器。

首次部署前，在服务器创建部署目录及仅供服务器使用的环境文件：

```bash
sudo mkdir -p /opt/xauat-eduapi
sudo chown "$USER" /opt/xauat-eduapi
cp .env.example /opt/xauat-eduapi/.env
sed -i 's/^ASPNETCORE_ENVIRONMENT=.*/ASPNETCORE_ENVIRONMENT=Production/' /opt/xauat-eduapi/.env
chmod 600 /opt/xauat-eduapi/.env
```

在 GitHub 仓库的 `Settings -> Environments -> production` 中配置以下 Secrets：

| Secret | 用途 |
|-------|------|
| `DEPLOY_HOST` | 服务器地址 |
| `DEPLOY_PORT` | SSH 端口，例如 `22` |
| `DEPLOY_USER` | 可运行 Docker 的 SSH 用户 |
| `DEPLOY_PATH` | 部署目录，例如 `/opt/xauat-eduapi` |
| `DEPLOY_SSH_PRIVATE_KEY` | 对应部署用户的 Ed25519 私钥 |
| `DEPLOY_KNOWN_HOSTS` | `ssh-keyscan -H <服务器地址>` 的输出 |
| `GHCR_PULL_TOKEN` | 仅具 `read:packages` 权限、可拉取该镜像的 GitHub token |

服务器应已安装 Docker Engine 和 Docker Compose 插件，且部署用户有运行 Docker 的权限。将首次生成的 GHCR 包设置为允许该仓库访问；若镜像设为公开，`GHCR_PULL_TOKEN` 仍可保留为最小权限 token。生产配置写在服务器 `/opt/xauat-eduapi/.env`，不要提交到仓库。可选的 `APP_PORT` 用于调整宿主机暴露端口，默认为 `8080`。

### 本地运行

```bash
cp .env.example .env
dotnet run --project XAUAT.EduApi
```

## API 接口文档

项目集成了 Scalar API 文档，启动后可通过 `/scalar/v1` 路径访问详细的接口文档。

### 日志查询

`GET /Logs?page=1&pageSize=50` 返回日志文件及当前运行日志，按时间倒序分页。可通过 `level`（最低级别）和 `search`（关键词）筛选。服务启动时会读取 `logs/log-*.txt`，运行期间继续写入按天轮转的日志文件，并保留最近 2000 条内存记录；配置 `LOG_VIEW_TOKEN` 后，请使用 `X-Log-Token` 请求头或 Bearer Token 访问。

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
