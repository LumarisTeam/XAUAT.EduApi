#!/usr/bin/env bash

PART="${PART:-${1:-28080}}"
if [[ ! "$PART" =~ ^[0-9]+$ ]] || (( PART < 1 || PART > 65535 )); then
  echo "错误：PART 必须是 1 到 65535 之间的端口号。"
  exit 1
fi

git pull
sudo docker stop xauat-eduapi
sudo docker rm xauat-eduapi
sudo docker build -t xauat-eduapi .
if [ ! -f ./prod.env ]; then
  echo "错误：未找到 ./prod.env，请先创建该文件再运行。"
  exit 1
fi
# 支付已拆到 XAUAT.PaymentAPI，EduApi 必须能通过共享网络访问到它，
# 且 prod.env 里要有 PAYMENT_API_BASE_URL（缺失时 EduApi 启动即失败）。
NETWORK_NAME="${NETWORK_NAME:-xauat-net}"
sudo docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || sudo docker network create "$NETWORK_NAME"
sudo docker run -d \
  --name xauat-eduapi \
  --network "$NETWORK_NAME" \
  -p "${PART}:8080" \
  --env-file ./prod.env \
  xauat-eduapi:latest
