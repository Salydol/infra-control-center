# Агент ICC

Один статический бинарник на Go. Сам подключается к центру (входящих портов
на сервере нет), собирает метрики хоста и контейнеров и инвентарь Docker/Compose.

## Регистрация и доверие

1. Администратор выпускает одноразовый токен в центре:
   ```bash
   docker compose exec backend dotnet Icc.ControlPlane.dll agents create-token --ttl-hours 24
   ```
   Токен: `icc1.<id>.<секрет>.<sha256 CA центра>`.
2. Агент запрашивает сертификат CA центра (`GetCenterInfo`) и сверяет его
   хеш с токеном — подменённый центр не пройдёт эту проверку.
3. Агент создаёт ключ ECDSA P-256 (ключ не покидает сервер) и отправляет CSR
   с токеном (`Register`). Центр гасит токен и подписывает сертификат с
   `CN=<id агента>` на 90 дней.
4. Дальше — только mTLS: поток `Connect` принимается лишь с сертификатом,
   выпущенным CA центра, и идентификатор агента берётся из сертификата.

Ключи хранятся в `--data-dir` (`/var/lib/icc-agent`): `agent.key`, `agent.crt`,
`ca.crt`, `agent.json`.

## Запуск на узле стенда

```bash
cd testbed/agent
ICC_CENTER=<адрес центра>:9090 ICC_TOKEN=<токен> docker compose up -d --build
```

Агент работает в сети хоста, читает `/proc`, `/sys` и корень хоста только на
чтение и Docker API через сокет. Контейнеры инжектора сбоев (метка
`icc.fault-injector=true`, образы Pumba и stress-ng) агент не видит.

| Параметр | Переменная | По умолчанию |
|---|---|---|
| `--center` | `ICC_CENTER` | `localhost:9090` |
| `--token` | `ICC_TOKEN` | — (нужен только при первом запуске) |
| `--data-dir` | `ICC_DATA_DIR` | `/var/lib/icc-agent` |

## Метрики

Интервалы задаёт центр (по умолчанию: метрики — 5 с, heartbeat — 10 с,
инвентарь — 30 с). У всех рядов есть метки `agent_id` и `host`.

| Хост | Контейнер (+ `container`, `compose_project`, `compose_service`, `image`) |
|---|---|
| `host_cpu_usage_ratio` | `container_cpu_usage_cores`, `container_cpu_throttled_seconds_total` |
| `host_load1/5/15` | `container_memory_usage_bytes`, `container_memory_limit_bytes` |
| `host_memory_{total,used,available}_bytes`, `host_swap_used_bytes` | `container_network_{receive,transmit}_{bytes,errors,drop}_total` |
| `host_disk_{total,used}_bytes{mountpoint}` | `container_fs_{read,write}_bytes_total` |
| `host_disk_{read,written}_bytes_total{device}`, `host_disk_io_time_seconds_total` | `container_pids`, `container_restarts`, `container_oom_killed` |
| `host_network_{receive,transmit}_{bytes,errors,drop}_total{interface}` | `container_up`, `container_health` (1 — healthy, 0 — unhealthy, 0.5 — starting, −1 — нет проверки) |

Состояние самого агента: `icc_agent_queue_messages`,
`icc_agent_dropped_messages_total`, `icc_agent_heap_bytes`.

## Разработка

```bash
go vet ./... && go test ./...
buf generate   # из корня репозитория, после изменения proto/
```
