#!/usr/bin/env bash
# Подготовка узла стенда (Ubuntu 24.04): Docker Engine, Compose, синхронизация
# времени и базовые настройки. Запускать от root:  sudo bash setup-node.sh <hostname>
set -euo pipefail

NEW_HOSTNAME="${1:-}"
if [[ $EUID -ne 0 ]]; then
  echo "Запустите от root: sudo bash $0 <hostname>" >&2
  exit 1
fi

if [[ -n "$NEW_HOSTNAME" ]]; then
  hostnamectl set-hostname "$NEW_HOSTNAME"
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q ca-certificates curl gnupg jq chrony

# Точное время на всех узлах обязательно: события изменений и метрики
# с разных ВМ сопоставляются по времени.
systemctl enable --now chrony

# Docker Engine из официального репозитория.
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list
apt-get update -q
apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Ротация логов контейнеров, чтобы стенд не забил диск за недели прогонов.
cat > /etc/docker/daemon.json <<'JSON'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "50m", "max-file": "5" }
}
JSON
systemctl restart docker

if [[ -n "${SUDO_USER:-}" ]]; then
  usermod -aG docker "$SUDO_USER"
fi

mkdir -p /opt/icc-testbed
echo "Готово: $(hostname), $(docker --version), $(docker compose version)"
