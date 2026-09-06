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
sudo docker run -d   --name xauat-eduapi   -p "${PART}:8080"   --env-file ./prod.env   xauat-eduapi:latest
