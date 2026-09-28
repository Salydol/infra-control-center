"""Каталог сценариев сбоев (6 классов) и безвредных изменений-приманок."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

from .actions import (
    Action,
    ContainerState,
    Deploy,
    EnvFileEdit,
    JsonConfigEdit,
    ManualEdit,
    NodeCpuHog,
    PumbaNetem,
    PumbaStress,
)

# Классы сбоев из ТЗ. Первые три связаны с изменением, остальные — нет.
CLASSES = {
    "misconfig": True,
    "bad_deploy": True,
    "manual_edit": True,
    "resource": False,
    "network": False,
    "dependency": False,
}


@dataclass(frozen=True)
class Scenario:
    id: str
    fault_class: str
    description: str
    # Фабрика: каждый инцидент получает свежий объект действия.
    make: Callable[[], Action]
    expected_symptoms: tuple[str, ...]

    @property
    def change_related(self) -> bool:
        return CLASSES[self.fault_class]


SCENARIOS: list[Scenario] = [
    # ---------- неверная настройка ----------
    Scenario(
        "M1",
        "misconfig",
        "api: Orders:MaxQuantity 10 → 1",
        lambda: JsonConfigEdit("api", {"Orders:MaxQuantity": 1}),
        ("HTTP 400 на POST /orders", "падение числа заказов"),
    ),
    Scenario(
        "M2",
        "misconfig",
        "payments-worker: таймаут провайдера 1000 → 40 мс",
        lambda: JsonConfigEdit("payments-worker", {"Payments:ProviderTimeoutMs": 40}),
        ("ошибки обработки payments", "сообщения в dead-letter", "заказы не оплачиваются"),
    ),
    Scenario(
        "M3",
        "misconfig",
        "notifications-worker: задержка отправки 30 → 1500 мс",
        lambda: JsonConfigEdit("notifications-worker", {"Notifications:SendDelayMs": 1500}),
        ("рост очереди notifications.order-paid",),
    ),
    Scenario(
        "M4",
        "misconfig",
        "api: таймаут catalog 2000 → 3 мс без повторов",
        lambda: JsonConfigEdit("api", {"Catalog:TimeoutMs": 3, "Catalog:Retries": 0}),
        ("HTTP 503 на POST /orders и GET /products", "таймауты catalog в логах api"),
    ),
    Scenario(
        "M5",
        "misconfig",
        "api: неверный адрес catalog",
        lambda: JsonConfigEdit("api", {"Catalog:BaseUrl": "http://catalog:8081"}),
        ("HTTP 503 почти на всех запросах", "connection refused в логах api"),
    ),
    Scenario(
        "M6",
        "misconfig",
        "payments-worker: неверный пароль RabbitMQ в env-файле",
        lambda: EnvFileEdit("payments-worker", "RabbitMq__Password", "wrong-password"),
        ("рост очереди payments.order-created", "ошибки подключения к RabbitMQ"),
    ),
    Scenario(
        "M7",
        "misconfig",
        "notifications-worker: неверный пароль БД в env-файле",
        lambda: EnvFileEdit("notifications-worker", "Database__Password", "wrong-password"),
        ("ошибки аутентификации PostgreSQL", "сообщения в dead-letter"),
    ),
    # ---------- неудачный деплой ----------
    Scenario(
        "D1",
        "bad_deploy",
        "api: релиз с медленным запросом при создании заказа",
        lambda: Deploy("api", "slow-order-query"),
        ("рост задержки POST /orders", "медленные запросы в логе PostgreSQL"),
    ),
    Scenario(
        "D2",
        "bad_deploy",
        "api: релиз падает при старте",
        lambda: Deploy("api", "startup-crash"),
        ("перезапуски api", "HTTP 502 на шлюзе"),
    ),
    Scenario(
        "D3",
        "bad_deploy",
        "catalog: релиз с неиндексированным запросом",
        lambda: Deploy("catalog", "unindexed-query"),
        ("рост задержки GET /products/{id}", "рост CPU PostgreSQL"),
    ),
    Scenario(
        "D4",
        "bad_deploy",
        "payments-worker: релиз с ошибкой разбора суммы",
        lambda: Deploy("payments-worker", "amount-parse-bug"),
        ("FormatException в логах", "сообщения в dead-letter"),
    ),
    Scenario(
        "D5",
        "bad_deploy",
        "notifications-worker: релиз с утечкой памяти",
        lambda: Deploy("notifications-worker", "memory-leak"),
        ("рост памяти", "OOMKilled и перезапуски"),
    ),
    # ---------- ручная правка по SSH ----------
    Scenario(
        "H1",
        "manual_edit",
        "compose.yaml: лимит памяти api 256M → 16M",
        lambda: ManualEdit(
            "api",
            "compose.yaml",
            [
                (
                    "    image: shop-api:${API_VERSION:-1.0.0}\n",
                    "    image: shop-api:${API_VERSION:-1.0.0}\n"
                    '    deploy:\n      resources:\n        limits: { cpus: "1.0", memory: 16M }\n',
                )
            ],
            follow_up="recreate",
        ),
        ("OOMKilled api", "HTTP 502 на шлюзе"),
    ),
    Scenario(
        "H2",
        "manual_edit",
        "nginx.conf: proxy_read_timeout 15s → 4ms",
        lambda: ManualEdit(
            "gateway",
            "gateway/nginx.conf",
            [("proxy_read_timeout 15s;", "proxy_read_timeout 4ms;")],
            follow_up="nginx-reload",
        ),
        ("HTTP 504 на шлюзе",),
    ),
    Scenario(
        "H3",
        "manual_edit",
        "compose.yaml: Redis maxmemory 128mb → 1mb без вытеснения",
        lambda: ManualEdit(
            "redis",
            "compose.yaml",
            [
                (
                    '"--maxmemory", "128mb", "--maxmemory-policy", "allkeys-lru"',
                    '"--maxmemory", "1mb", "--maxmemory-policy", "noeviction"',
                )
            ],
            follow_up="recreate",
        ),
        ("OOM command not allowed в Redis", "ошибки notifications-worker"),
    ),
    Scenario(
        "H4",
        "manual_edit",
        "compose.yaml: PostgreSQL max_connections 100 → 12",
        lambda: ManualEdit(
            "postgres",
            "compose.yaml",
            [('"-c", "max_connections=100"', '"-c", "max_connections=12"')],
            follow_up="recreate",
        ),
        ("too many clients already", "ошибки 5xx во всех сервисах"),
    ),
    # ---------- ресурсы ----------
    Scenario(
        "R1",
        "resource",
        "CPU-нагрузка в контейнере PostgreSQL",
        lambda: PumbaStress("postgres", "--cpu 4"),
        ("рост задержки запросов к БД",),
    ),
    Scenario(
        "R2",
        "resource",
        "CPU-нагрузка в контейнере catalog",
        lambda: PumbaStress("catalog", "--cpu 2"),
        ("рост задержки catalog и api",),
    ),
    Scenario(
        "R3",
        "resource",
        "Нехватка памяти в контейнере payments-worker",
        lambda: PumbaStress("payments-worker", "--vm 2 --vm-bytes 120M"),
        ("рост памяти", "возможен OOMKilled"),
    ),
    Scenario(
        "R4",
        "resource",
        "Шумный сосед: нагрузка CPU на весь узел api",
        lambda: NodeCpuHog("api"),
        ("рост задержки всех сервисов узла",),
    ),
    # ---------- сеть ----------
    Scenario(
        "N1",
        "network",
        "Задержка 300±50 мс на catalog",
        lambda: PumbaNetem("catalog", delay_ms=300, jitter_ms=50),
        ("рост задержки GET /products и POST /orders",),
    ),
    Scenario(
        "N2",
        "network",
        "Потеря 30% пакетов у PostgreSQL",
        lambda: PumbaNetem("postgres", loss_percent=30),
        ("таймауты запросов к БД", "ошибки 5xx"),
    ),
    Scenario(
        "N3",
        "network",
        "Задержка 400±100 мс у Redis",
        lambda: PumbaNetem("redis", delay_ms=400, jitter_ms=100),
        ("таймауты Redis", "деградация catalog на БД"),
    ),
    Scenario(
        "N4",
        "network",
        "Потеря 70% пакетов у RabbitMQ",
        lambda: PumbaNetem("rabbitmq", loss_percent=70),
        ("ошибки публикации", "рост задержки обработки"),
    ),
    # ---------- падение зависимости ----------
    Scenario(
        "P1",
        "dependency",
        "Падение Redis",
        lambda: ContainerState("redis", "kill"),
        ("ошибки Redis", "ошибки notifications-worker"),
    ),
    Scenario(
        "P2",
        "dependency",
        "Зависание PostgreSQL",
        lambda: ContainerState("postgres", "pause"),
        ("таймауты БД во всех сервисах",),
    ),
    Scenario(
        "P3",
        "dependency",
        "Падение RabbitMQ",
        lambda: ContainerState("rabbitmq", "kill"),
        ("ошибки публикации", "HTTP 503 на POST /orders"),
    ),
    Scenario(
        "P4",
        "dependency",
        "Зависание catalog",
        lambda: ContainerState("catalog", "pause"),
        ("таймауты catalog", "HTTP 503 в api"),
    ),
]

# Безвредные изменения: делаются незадолго до сбоя, не связанного с изменением,
# чтобы проверить, что метод не винит «последнее изменение» автоматически.
DECOYS: list[tuple[str, str, Callable[[], Action]]] = [
    ("X1", "catalog: MaxPageSize 50 → 60", lambda: JsonConfigEdit("catalog", {"Catalog:MaxPageSize": 60})),
    (
        "X2",
        "notifications-worker: DedupTtlSeconds 3600 → 7200",
        lambda: JsonConfigEdit("notifications-worker", {"Notifications:DedupTtlSeconds": 7200}),
    ),
    ("X3", "api: RetryDelayMs 100 → 150", lambda: JsonConfigEdit("api", {"Catalog:RetryDelayMs": 150})),
    (
        "X4",
        "payments-worker: CommandTimeoutSeconds 10 → 15 с рестартом",
        lambda: JsonConfigEdit("payments-worker", {"Database:CommandTimeoutSeconds": 15}, restart=True),
    ),
    ("X5a", "api: обычный релиз", lambda: Deploy("api")),
    ("X5b", "catalog: обычный релиз", lambda: Deploy("catalog")),
    ("X5c", "payments-worker: обычный релиз", lambda: Deploy("payments-worker")),
    ("X5d", "notifications-worker: обычный релиз", lambda: Deploy("notifications-worker")),
    ("X6", "catalog: новый флаг в env-файле", lambda: EnvFileEdit("catalog", "FEATURE_RECOMMENDATIONS", "false")),
    (
        "X7",
        "nginx.conf: заголовок X-Served-By",
        lambda: ManualEdit(
            "gateway",
            "gateway/nginx.conf",
            [
                (
                    "        proxy_set_header X-Request-Id $request_id;\n",
                    "        proxy_set_header X-Request-Id $request_id;\n"
                    "        add_header X-Served-By gateway always;\n",
                )
            ],
            follow_up="nginx-reload",
        ),
    ),
    (
        "X8",
        "compose.yaml: правка комментария без применения",
        lambda: ManualEdit(
            "gateway", "compose.yaml", [("# Демо-магазин стенда.\n", "# Демо-магазин стенда (ICC testbed).\n")]
        ),
    ),
]


def by_id(scenario_id: str) -> Scenario:
    for scenario in SCENARIOS:
        if scenario.id == scenario_id:
            return scenario
    raise KeyError(f"unknown scenario {scenario_id}")


def decoy_by_id(decoy_id: str) -> tuple[str, str, Callable[[], Action]]:
    for decoy in DECOYS:
        if decoy[0] == decoy_id:
            return decoy
    raise KeyError(f"unknown decoy {decoy_id}")
