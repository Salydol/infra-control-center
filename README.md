# Infrastructure Control Center (ICC)

Дипломная работа: **«Интеллектуальный анализ первопричин инцидентов в контейнерной
инфраструктуре с учётом истории изменений»** (Change-Aware Root Cause Analysis).

Вопрос исследования: насколько учёт полной истории изменений (деплои, правки
настроек, ручные правки) повышает точность поиска первопричины сбоя в небольшой
Docker-инфраструктуре.

## Структура

| Папка | Что внутри | Стек |
|---|---|---|
| `proto/` | Контракты агент ↔ центр | Protobuf, Buf |
| `agent/` | Агент на серверах: метрики, логи, события изменений | Go |
| `backend/` | Control plane: приём данных, REST API, таймлайн, инциденты | .NET 10, ASP.NET Core, gRPC |
| `ai/` | Детектор аномалий, шаблоны логов, ранжирование причин, LLM | Python 3.12, FastAPI |
| `web/` | Интерфейс | React, TypeScript, Vite |
| `testbed/` | Стенд: 3 ВМ, демо-приложение, нагрузка, инжектор сбоев | Docker, k6, Pumba |
| `experiments/` | Эксперименты и статистика | Jupyter, pandas, scipy |
| `thesis/` | Текст диплома | |

## Запуск центра

```bash
docker compose up -d --build
```

- Интерфейс: http://localhost:8088
- gRPC для агентов: `localhost:9090`
- VictoriaMetrics: http://localhost:8428, VictoriaLogs: http://localhost:9428
- PostgreSQL центра: `localhost:15432` (не 5432 — чтобы не конфликтовать с локальным)

Порты меняются в `.env` (см. `.env.example`).

## Подключение сервера

```bash
# токен регистрации (одноразовый)
docker compose exec backend dotnet Icc.ControlPlane.dll agents create-token

# на сервере
cd testbed/agent && ICC_CENTER=<центр>:9090 ICC_TOKEN=<токен> docker compose up -d --build
```

Подробнее — [agent/README.md](agent/README.md). Стенд, нагрузка и инжектор
сбоев — [testbed/README.md](testbed/README.md), [testbed/faults/README.md](testbed/faults/README.md).

## Разработка

```bash
# Контракты: проверка и генерация Go-кода
buf lint && buf format -w && buf generate

# Backend
cd backend && dotnet build Icc.slnx && dotnet test --solution Icc.slnx

# AI-модуль
cd ai && python -m venv .venv && .venv/Scripts/pip install -e ".[dev]" && .venv/Scripts/pytest

# Web (dev-сервер проксирует /api на localhost:8080)
cd web && npm install && npm run dev

# Агент
cd agent && go build ./cmd/icc-agent
```

## Календарный план

| Дата | Контрольная точка |
|---|---|
| 11.10.2026 | Стенд работает |
| 01.11.2026 | Агент собирает все данные |
| 29.11.2026 | Платформа и AI-модуль готовы, 120+ инцидентов |
| 13.12.2026 | Главная таблица результатов |
| 20.12.2026 | Версия 1.0 заморожена |
