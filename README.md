# XAUAT EduApi

西安建筑科技大学教务系统 API 接口项目，提供对学校教务系统的数据访问接口。

## 项目简介

这是一个基于 ASP.NET Core 的 Web API 项目，旨在为西安建筑科技大学（XAUAT）的教务系统提供统一的数据访问接口。该项目通过模拟登录和数据抓取，为移动应用和其他客户端提供标准化的 RESTful API 服务。

## 功能特性

本项目提供了以下主要功能模块：

### 教务相关
- **登录认证** - 用户登录验证
- **通用统一身份认证** - 只填目标 URL + 学号密码，自动发现 CAS 并换取 cookie（`POST /v1/login/unified`）
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
| UNIFIED_AUTH_ALLOWED_HOSTS | 通用统一身份认证允许访问的目标域名（逗号分隔，支持 `*.域` 与 `*`；内置默认 `*.edu.cn` 且只能追加） | `*.example.com` |
| LOG_VIEW_TOKEN | 日志接口访问令牌（设置后启用鉴权） | `change-me` |

## 部署方式

### CI：只构建镜像，不部署

`.github/workflows/deploy-production.yml` 在推送到 `master` 时先跑测试，再把镜像推送到 GHCR
（`ghcr.io/lumaristeam/xauat.eduapi`，tag 为本次 commit SHA 与 `latest`）。
**它不会碰服务器**——部署由人工在服务器上执行，发布时机因此由你决定，CI 也不必持有服务器私钥。

### 服务器部署

本服务是三个服务里唯一映射宿主机端口的一个（对外提供 API），另外两个只经共享网络被调用。

首次部署前，把部署目录所需的三样东西放到服务器上：

```bash
sudo mkdir -p /opt/xauat-eduapi/deploy
sudo chown -R "$USER" /opt/xauat-eduapi
cd /opt/xauat-eduapi/deploy

# 1) compose 定义与部署脚本（从仓库 deploy/ 目录复制）
scp <本机>:XAUAT.EduApi/deploy/docker-compose.production.yml .
scp <本机>:XAUAT.EduApi/deploy/build_from_ghcr.sh .
chmod +x build_from_ghcr.sh

# 2) 环境文件
curl -fsSL <仓库里的 .env.example> -o .env
sed -i 's/^ASPNETCORE_ENVIRONMENT=.*/ASPNETCORE_ENVIRONMENT=Production/' .env
sed -i 's#^PAYMENT_API_BASE_URL=.*#PAYMENT_API_BASE_URL=http://xauat-paymentapi:8080#' .env
chmod 600 .env
```

`PAYMENT_API_BASE_URL` 是必需项，缺失时 EduApi 启动即失败。登录没有上游服务——EduApi 自己
做 CAS/UAAP 握手，`REDIS` 就是登录缓存与封禁状态的存放处（留空则退化为无缓存、无限流）。

之后每次发布：

```bash
cd /opt/xauat-eduapi/deploy
./build_from_ghcr.sh                                            # 拉 :latest
./build_from_ghcr.sh ccr.ccs.tencentyun.com/lumaris/xauat.eduapi:<sha>   # 指定版本，也是回滚方式
APP_PORT=9090 ./build_from_ghcr.sh                              # 改宿主机端口（默认 8080）
```

脚本会幂等创建共享网络 `xauat-net`、拉取镜像、`docker compose up -d`，并等到日志里出现
`Now listening on` 才报成功；末尾会打印自检命令与准确的回滚命令。

### 镜像仓库与凭据

CI 把同一批 tag 推**两份**：ghcr.io 作归档，腾讯云 TCR 供国内服务器拉取。
服务器默认从 TCR 拉——ghcr 的镜像层走 `pkg-containers.githubusercontent.com`，
在国内基本拉不动（命令能通、认证也能过，就是层下不来）。

TCR 是私有仓库，服务器需要一组凭据：

1. 腾讯云控制台 → 容器镜像服务 TCR → 个人版实例 → 实例管理 → **初始化密码**
   （忘了就「更多 → 重置登录密码」）
2. **用户名是当前登录的腾讯云账号 ID**
3. 部署时传入：

```bash
PULL_TOKEN=<你设的固定密码> PULL_USERNAME=<腾讯云账号ID> ./build_from_ghcr.sh
```

脚本用完会 `docker logout <registry>`；不传这两个变量则沿用本机已有的 docker 凭据
（即此前手动 `docker login ccr.ccs.tencentyun.com` 过）。

要改用 ghcr 那份就加 `USE_GHCR=1`，凭据换成一张 GitHub classic PAT
（**只勾 `read:packages`**——GitHub Packages 不支持 fine-grained token）：

```bash
USE_GHCR=1 PULL_TOKEN=<classic PAT> PULL_USERNAME=<你的 GitHub 用户名> ./build_from_ghcr.sh
```

### 从源码构建（备选）

服务器上没有 registry 凭据、或就是要跑当前工作区代码时，用仓库根目录的 `build.sh`：
`git pull` → `docker build` → 换掉旧容器，环境文件是 `prod.env`。它与 `deploy/build_from_ghcr.sh`
是两条并行路径，容器名同为 `xauat-eduapi`，互相不能叠加，切换前需 `docker rm -f xauat-eduapi`。

服务器需已安装 Docker Engine 与 Compose v2 插件，且执行用户有运行 Docker 的权限。

### 本地运行

```bash
cp .env.example .env
dotnet run --project XAUAT.EduApi
```

## API 接口文档

项目集成了 Scalar API 文档，启动后可通过 `/scalar/v1` 路径访问详细的接口文档。

### 通用统一身份认证

`POST /v1/login/unified` —— 只要目标系统用 CAS / 金智 UAAP 做统一认证，填一个受保护的地址加学号密码就能换到 cookie：

```bash
curl -X POST http://localhost:5216/v1/login/unified \
  -H 'Content-Type: application/json' \
  -d '{"url":"https://swjw.xauat.edu.cn/student/index","username":"2023xxxx","password":"..."}'
```

服务端会依次：请求该地址 → 顺着 302 **以及页面里的前端跳转线索**找到认证服务器登录页（连 `service` 参数一起）→ 按页面隐藏域与 `pwdEncryptSalt` 组装表单并登录 → 沿原路跳回，把沿途 cookie 一并返回。响应里的 `cookie` 是**落地站点**那一份会话 cookie，可直接用于后续抓取；`cookies` 是整条链路的全量明细（含 CASTGC 票据）。

> 为什么要读页面线索：新版树维/金智教务前台（如 `swjw.xauat.edu.cn/student/index`）不会一路 302 到 CAS，中间会回一个 200 的前端空壳（`/student/login`），真正的 SSO 入口写在页面 JS 的 `ssoLinkList` 里。只跟 302 的实现会停在这一跳，误报"该地址未跳转到统一身份认证登录页"。

几点约定：

- **目标域名白名单**：默认只放行 `*.edu.cn`，用 `UNIFIED_AUTH_ALLOWED_HOSTS` 追加（只能加不能减）。
- **内网地址一律拒绝**：校验发生在建立 TCP 连接之前，合法域名被解析到 `127.0.0.1` / `10.x` / `169.254.169.254` 同样会被拦下。
- **不接缓存**：与 `POST /v1/login` 不同，这个接口每次都真实登录一次（cookie 自然轮换），不写 Redis、不计入封禁体系。
- 失败映射：请求不合法 → 400，用户名密码错 → 401，目标站点不可达 → 502，并发限流 → 429。

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
