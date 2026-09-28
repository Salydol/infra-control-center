# Стенд

Демо-магазин из 8 сервисов на трёх ВМ, генератор нагрузки (k6) и
[инжектор сбоев](faults/README.md) шести классов с приманками и
автоматической разметкой.

## Демо-приложение

```
k6 ──► gateway (nginx) ──► api ──HTTP──► catalog ──► postgres
                            │              └──────► redis (кэш)
                            ├──► postgres
                            └──► rabbitmq ──► payments-worker ──► postgres
                                                  └──► rabbitmq ──► notifications-worker ──► postgres, redis
```

| Сервис | Узел | Что делает |
|---|---|---|
| `gateway` | node1 | nginx, входная точка, access-лог с временем апстрима |
| `api` | node1 | Заказы: резерв в catalog → запись в БД → событие `order.created` |
| `catalog` | node1 | Товары, кэш в Redis (при отказе Redis — деградация на БД), резерв остатков |
| `rabbitmq` | node1 | Очереди `payments.order-created`, `notifications.order-paid`, dead-letter |
| `payments-worker` | node2 | Имитация платёжного провайдера (задержка, таймаут, отказы) → `order.paid` |
| `notifications-worker` | node2 | Уведомления с дедупликацией в Redis |
| `redis` | node2 | Кэш и дедупликация |
| `postgres` | node3 | Заказы, платежи, уведомления, 2000 товаров |

Все .NET-сервисы отдают `/healthz` и `/metrics` (Prometheus): HTTP-метрики,
`shop_dependency_duration_seconds{dependency,operation,result}` — время и исход
каждого обращения к БД, Redis, catalog, RabbitMQ, провайдеру,
`shop_messages_processed_total`, `shop_orders_total`, `shop_cache_requests_total`.

### Настройки — рычаги для сбоев класса «неверная настройка»

Источники по возрастанию приоритета: `appsettings.json` в образе →
`deploy/config/<сервис>/appsettings.json` с хоста → `deploy/env/<сервис>.env`
и `environment` в compose.

| Сервис | Настройка | Применение |
|---|---|---|
| api | `Catalog:TimeoutMs`, `Catalog:Retries`, `Orders:MaxQuantity` | на лету |
| api, catalog, воркеры | `Database:MaxPoolSize`, `Database:*Timeout*` | рестарт |
| catalog | `Cache:Enabled`, `Cache:TtlSeconds`, `Catalog:MaxPageSize` | на лету |
| payments-worker | `Payments:ProviderTimeoutMs`, `ProviderLatencyMs`, `ProviderDeclineRate` | на лету |
| payments-worker | `Payments:Prefetch`, `Payments:Concurrency` | рестарт |
| notifications-worker | `Notifications:SendDelayMs`, `Channel` | на лету |
| все | `Logging__LogLevel__Default` (env) | рестарт |

Лимиты ресурсов (`deploy.resources.limits` в compose) — рычаг для сбоев
класса «ресурсы» (OOM, троттлинг CPU).

## Запуск на одной машине

```bash
cd testbed/app/deploy
cp .env.example .env          # COMPOSE_PROFILES=node1,node2,node3
docker compose up -d --build --wait

cd ../../load
docker compose up -d          # нагрузка на http://localhost:8000
```

## Развёртывание на трёх ВМ

| ВМ | Профиль | Ресурсы (минимум) |
|---|---|---|
| `icc-node1` | `node1` | 2 vCPU, 2 ГБ RAM, 20 ГБ диск |
| `icc-node2` | `node2` | 2 vCPU, 2 ГБ RAM, 20 ГБ диск |
| `icc-node3` | `node3` + нагрузка | 2 vCPU, 2 ГБ RAM, 20 ГБ диск |

ОС — Ubuntu Server 24.04 LTS. На каждой ВМ:

```bash
sudo bash vm/setup-node.sh icc-node1          # Docker, chrony
git clone https://github.com/Salydol/infra-control-center.git
cd infra-control-center/testbed/app/deploy
cp .env.example .env    # COMPOSE_PROFILES=node1 (node2, node3), NODE*_IP — адреса ВМ
docker compose -f compose.yaml -f compose.distributed.yaml up -d --build --wait
```

На node3 дополнительно:

```bash
cd ../../load && BASE_URL=http://<NODE1_IP>:8000 docker compose up -d
```

## Нагрузка

`load/shop.js`: 55% карточек товаров (горячие товары чаще), 15% поиска,
25% заказов, 5% просмотров заказов. Интенсивность — синусоида от 0,5 до 1,5
от `BASE_RPS` с периодом `CYCLE_MINUTES`.
