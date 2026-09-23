#!/usr/bin/env bash
#
# 服务器单例部署：从 ghcr 拉取预构建镜像，复用本目录的 compose 定义起一个容器。
#
# 为什么是"拉"而不是"构建"：在服务器上从源码构建要拉 SDK 镜像并跑一次完整的
# dotnet publish，慢且吃满 CPU；而 CI（.github/workflows/deploy-production.yml）
# 已经在 ubuntu-latest 上构建并推送了镜像。本脚本只负责拉取与重启。
#
# 与仓库根目录 build.sh 是两条并行路径，别混着用：
#   本脚本     从 ghcr 拉 CI 构建好的不可变镜像，用 compose 起。部署默认走这条。
#   根目录     从源码构建本地镜像并起容器（走 prod.env），供服务器上没有 ghcr 凭据时应急。
#
# 两条路径的容器名都是 xauat-eduapi，互相不能叠加：根目录脚本起的是 docker run 直接创建的
# 容器，不带 compose 标签，之后再用本脚本会因容器名冲突而失败，需要先
# `docker rm -f xauat-eduapi`（脚本末尾会把这条命令打出来）。
#
# 与 LoginApi / PaymentAPI 的差异：本服务是三个里唯一映射宿主机端口的（对外提供 API），
# 因此下面能直接用 localhost 自检；另两个刻意不映射端口，只能经共享网络访问。
#
# 用法：
#   ./build_from_ghcr.sh
#   ./build_from_ghcr.sh ghcr.io/lumaristeam/xauat.eduapi:<commit-sha>   # 指定版本，也是回滚方式
#   APP_PORT=9090 ./build_from_ghcr.sh
#   IMAGE=... NETWORK_NAME=... COMPOSE_PROJECT_NAME=... ./build_from_ghcr.sh
#
# 同目录必须有：
#   docker-compose.yml 或 docker-compose.production.yml   （两种名字都认）
#   .env                                                   （见仓库根的 .env.example）

set -euo pipefail

CONTAINER_NAME="xauat-eduapi"
SERVICE_NAME="app"

DEFAULT_IMAGE="ghcr.io/lumaristeam/xauat.eduapi:latest"
IMAGE="${IMAGE:-${1:-$DEFAULT_IMAGE}}"

NETWORK_NAME="${NETWORK_NAME:-xauat-net}"
APP_PORT="${APP_PORT:-8080}"
READY_TIMEOUT="${READY_TIMEOUT:-60}"

# compose 的 project 名默认取目录名。这里显式固定，否则固定 container_name 会与旧 project 撞名。
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-xauat-eduapi}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# 显式导出：compose 插值优先级是 shell 环境 > 同目录 .env
export IMAGE NETWORK_NAME APP_PORT COMPOSE_PROJECT_NAME

# ------------------------------------------------------------------ 前置检查

command -v docker >/dev/null 2>&1 || { echo "错误：未找到 docker。" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "错误：需要 docker compose v2 插件（docker compose，而非旧版 docker-compose）。" >&2; exit 1; }

if [[ ! "$APP_PORT" =~ ^[0-9]+$ ]] || (( APP_PORT < 1 || APP_PORT > 65535 )); then
  echo "错误：APP_PORT 必须是 1 到 65535 之间的端口号，收到：'$APP_PORT'" >&2
  exit 1
fi

COMPOSE_FILE=""
for candidate in docker-compose.yml docker-compose.yaml docker-compose.production.yml; do
  if [[ -f "$candidate" ]]; then COMPOSE_FILE="$candidate"; break; fi
done
if [[ -z "$COMPOSE_FILE" ]]; then
  echo "错误：$SCRIPT_DIR 下找不到 docker-compose.yml 或 docker-compose.production.yml。" >&2
  exit 1
fi

if [[ ! -f .env ]]; then
  cat >&2 <<'ENV_HELP'
错误：未找到 .env。compose 的 env_file 指向它，缺失时 docker compose 会直接失败。

请在当前目录创建 .env，至少包含：
  ASPNETCORE_ENVIRONMENT=Production
  PAYMENT_API_BASE_URL=http://xauat-paymentapi:8080   # 必需，缺失时启动即失败
  LOGIN_API_BASE_URL=                                 # 留空 = 直接打 Flask；填了 = 转发到 XAUAT.LoginApi
  SQL=                                                # 留空 = SQLite；生产建议填 PostgreSQL
  REDIS=                                              # 留空 = 不启用 Redis

完整说明见仓库根目录的 .env.example。注意 .env 不要提交进 git。
ENV_HELP
  exit 1
fi

if docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
  owner="$(docker container inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$CONTAINER_NAME" 2>/dev/null || true)"
  [[ "$owner" == "<no value>" ]] && owner=""
  if [[ -n "$owner" && "$owner" != "$COMPOSE_PROJECT_NAME" ]]; then
    cat >&2 <<CONFLICT
错误：容器 $CONTAINER_NAME 已存在，但属于另一个 compose project「${owner}」。

compose 不会接管别的 project 的容器。二选一：
  docker rm -f $CONTAINER_NAME          # 让本脚本接管
  COMPOSE_PROJECT_NAME=$owner ./build_from_ghcr.sh   # 沿用那个 project
CONFLICT
    exit 1
  fi
fi

# ------------------------------------------------------------------ 共享网络

# EduApi 是调用方：它靠 xauat-paymentapi / xauat-loginapi 这两个名字解析到另外两个服务，
# 三方必须在同一张网络上。
if ! docker network inspect "$NETWORK_NAME" >/dev/null 2>&1; then
  echo "==> 创建共享网络 $NETWORK_NAME"
  docker network create "$NETWORK_NAME" >/dev/null
fi

# ------------------------------------------------------------------ ghcr 登录

# 镜像若为私有，需要凭据。优先用传入的 token（用完即登出）；否则沿用本机已有的
# docker 凭据（此前手动 docker login ghcr.io 过就行）。
if [[ -n "${GHCR_PULL_TOKEN:-}" ]]; then
  : "${GHCR_USERNAME:?设置了 GHCR_PULL_TOKEN 就必须同时设置 GHCR_USERNAME}"
  trap 'docker logout ghcr.io >/dev/null 2>&1 || true' EXIT
  printf '%s' "$GHCR_PULL_TOKEN" | docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin
fi

# ------------------------------------------------------------------ 拉取与启动

# 先记下当前在跑的镜像，末尾用它给出准确的回滚命令
previous_image="$(docker container inspect -f '{{.Config.Image}}' "$CONTAINER_NAME" 2>/dev/null || true)"

echo "==> 拉取镜像 $IMAGE"
docker compose -f "$COMPOSE_FILE" pull "$SERVICE_NAME"

echo "==> 启动容器（project=${COMPOSE_PROJECT_NAME}）"
docker compose -f "$COMPOSE_FILE" up -d --no-build --remove-orphans "$SERVICE_NAME"

# ------------------------------------------------------------------ 就绪等待

# 启动即失败的两大原因：.env 里 PAYMENT_API_BASE_URL 缺失（配置校验直接抛），
# 以及 REDIS 填了地址却连不上（abortConnect 默认 true，进程退出而不是降级）。
# 所以不只看容器在不在跑，还要等它真的监听起来。
echo "==> 等待服务就绪（最多 ${READY_TIMEOUT}s）"
deadline=$(( SECONDS + READY_TIMEOUT ))
ready=0
while (( SECONDS < deadline )); do
  running="$(docker container inspect -f '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null || echo false)"
  if [[ "$running" != "true" ]]; then
    echo "错误：容器 $CONTAINER_NAME 已退出。日志：" >&2
    docker logs --tail 60 "$CONTAINER_NAME" >&2 || true
    exit 1
  fi

  # 用 bash 字符串匹配而不是管道 grep：pipefail 下 grep -q 提前退出会让 docker logs 吃到 SIGPIPE。
  logs="$(docker logs "$CONTAINER_NAME" 2>&1 || true)"
  if [[ "$logs" == *"Now listening on"* || "$logs" == *"Application started"* ]]; then
    ready=1
    break
  fi
  sleep 1
done

if (( ! ready )); then
  echo "错误：${READY_TIMEOUT}s 内没等到启动完成。最近日志：" >&2
  docker logs --tail 60 "$CONTAINER_NAME" >&2 || true
  exit 1
fi

# ------------------------------------------------------------------ 收尾

# 只清 dangling 镜像（不带 -a，不会动还有 tag 的镜像）。
docker image prune --force >/dev/null

digest="$(docker image inspect --format '{{index .RepoDigests 0}}' "$IMAGE" 2>/dev/null || true)"

echo
echo "==> 部署完成"
echo "    容器 : $CONTAINER_NAME"
echo "    镜像 : $IMAGE"
if [[ -n "$digest" ]]; then echo "    摘要 : $digest"; fi
echo "    网络 : ${NETWORK_NAME}"
echo "    端口 : 宿主机 ${APP_PORT} -> 容器 8080"
echo
# 本镜像是 aspnet:10.0（Debian，带 shell），与 LoginApi/PaymentAPI 的 chiseled 不同，
# 因此 docker exec 可用。下面那条自检是给宿主机用的，容器内有没有 curl 不作保证。
echo "    自检 : curl -fsS http://localhost:${APP_PORT}/openapi/v1.json >/dev/null && echo ok"
echo "           （本服务没有 /health 端点，openapi 是唯一无副作用的探活目标）"
echo "    排障 : docker logs -f $CONTAINER_NAME"
echo "           docker exec -it $CONTAINER_NAME bash"
if [[ -n "$previous_image" && "$previous_image" != "$IMAGE" ]]; then
  echo "    回滚 : $0 $previous_image"
fi
