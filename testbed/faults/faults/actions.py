"""Обратимые действия над стендом.

Каждое действие:
  prepare()  — долгая подготовка до момента внедрения (сборка образа);
  apply()    — внедрение; возвращает JSON-сериализуемую запись для отката;
  revert()   — откат по этой записи (работает и после падения инжектора);
  describe() — описание для разметки инцидента.
"""

from __future__ import annotations

import json
import re
import uuid
from dataclasses import dataclass
from typing import Any, ClassVar

from .topology import DOTNET_PROJECTS, VERSION_VARS, CommandError, Topology, container_name

PUMBA_IMAGE = "gaiaadm/pumba:1.2.1"
STRESS_IMAGE = "ghcr.io/alexei-led/stress-ng:latest"
NETTOOLS_IMAGE = "ghcr.io/alexei-led/pumba-alpine-nettools:latest"
# Метка вспомогательных контейнеров инжектора: агент ICC их игнорирует,
# иначе модель научится находить причину по появлению контейнера Pumba.
INJECTOR_LABEL = "icc.fault-injector=true"


@dataclass
class Context:
    topology: Topology
    incident_id: str
    hold_seconds: int
    state: dict[str, Any]  # общее состояние кампании (счётчик версий и т.п.)


# ---------- вспомогательные функции ----------


def get_nested(data: dict[str, Any], key: str) -> Any:
    node: Any = data
    for part in key.split(":"):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def set_nested(data: dict[str, Any], key: str, value: Any) -> None:
    parts = key.split(":")
    node = data
    for part in parts[:-1]:
        node = node.setdefault(part, {})
    node[parts[-1]] = value


def set_env_line(text: str, key: str, value: str | None) -> tuple[str, str | None]:
    """Меняет KEY=value в env-файле (None — удалить). Возвращает текст и прежнее значение."""
    lines = text.splitlines()
    before = None
    out = []
    for line in lines:
        if re.match(rf"^\s*{re.escape(key)}=", line):
            before = line.split("=", 1)[1]
            if value is not None:
                out.append(f"{key}={value}")
            continue
        out.append(line)
    if before is None and value is not None:
        out.append(f"{key}={value}")
    return "\n".join(out) + "\n", before


def _fmt(value: Any) -> str:
    return json.dumps(value) if not isinstance(value, str) else value


def _cleanup_helpers(topology: Topology, node_name: str) -> None:
    node = topology.nodes[node_name]
    for image in (STRESS_IMAGE, NETTOOLS_IMAGE):
        ids = node.run(["docker", "ps", "-aq", "--filter", f"ancestor={image}"], check=False).stdout.split()
        if ids:
            node.run(["docker", "rm", "-f", *ids], check=False)


# ---------- базовый класс ----------


class Action:
    # Категория для разметки: config, env, deploy, manual, network, resource, dependency.
    category: ClassVar[str]
    # Порождает ли действие изменение конфигурации, видимое агенту.
    change_related: ClassVar[bool]

    service: str

    def prepare(self, ctx: Context) -> None:
        pass

    def apply(self, ctx: Context) -> dict[str, Any]:
        raise NotImplementedError

    @staticmethod
    def revert(topology: Topology, undo: dict[str, Any]) -> None:
        raise NotImplementedError

    def describe(self, topology: Topology) -> dict[str, Any]:
        raise NotImplementedError

    def touched_files(self) -> set[str]:
        return set()


def _restore_file_and_follow(topology: Topology, undo: dict[str, Any]) -> None:
    node = topology.nodes[undo["node"]]
    node.write_text(undo["file"], undo["original"])
    _follow_up(topology, undo["node"], undo["service"], undo["follow_up"])


def _follow_up(topology: Topology, node_name: str, service: str, follow_up: str | None) -> None:
    node = topology.nodes[node_name]
    if follow_up == "restart":
        node.compose("restart", service)
    elif follow_up == "recreate":
        node.compose("up", "-d", "--no-deps", "--no-build", service)
    elif follow_up == "nginx-reload":
        node.compose("exec", "-T", service, "nginx", "-s", "reload")
    elif follow_up is not None:
        raise ValueError(f"unknown follow-up {follow_up}")


# ---------- изменения конфигурации ----------


@dataclass
class JsonConfigEdit(Action):
    """Правка «живого» appsettings.json сервиса: config/<service>/appsettings.json."""

    category: ClassVar[str] = "config"
    change_related: ClassVar[bool] = True

    service: str
    values: dict[str, Any]
    restart: bool = False

    @property
    def file(self) -> str:
        return f"config/{self.service}/appsettings.json"

    def touched_files(self) -> set[str]:
        return {self.file}

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        original = node.read_text(self.file)
        data = json.loads(original)
        self._before = {k: get_nested(data, k) for k in self.values}
        for key, value in self.values.items():
            set_nested(data, key, value)
        undo = {
            "type": "JsonConfigEdit",
            "node": node.name,
            "service": self.service,
            "file": self.file,
            "original": original,
            "follow_up": "restart" if self.restart else None,
        }
        node.write_text(self.file, json.dumps(data, indent=2, ensure_ascii=False) + "\n")
        _follow_up(ctx.topology, node.name, self.service, undo["follow_up"])
        return undo

    revert = staticmethod(_restore_file_and_follow)

    def describe(self, topology: Topology) -> dict[str, Any]:
        before = getattr(self, "_before", {})
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.service],
            "file": self.file,
            "applied_by": "restart" if self.restart else "hot-reload",
            "changes": [{"key": k, "before": _fmt(before.get(k)), "after": _fmt(v)} for k, v in self.values.items()],
        }


@dataclass
class EnvFileEdit(Action):
    """Правка env-файла сервиса (env/<service>.env) с пересозданием контейнера."""

    category: ClassVar[str] = "env"
    change_related: ClassVar[bool] = True

    service: str
    key: str
    value: str | None

    @property
    def file(self) -> str:
        return f"env/{self.service}.env"

    def touched_files(self) -> set[str]:
        return {self.file}

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        original = node.read_text(self.file)
        updated, self._before = set_env_line(original, self.key, self.value)
        undo = {
            "type": "EnvFileEdit",
            "node": node.name,
            "service": self.service,
            "file": self.file,
            "original": original,
            "follow_up": "recreate",
        }
        node.write_text(self.file, updated)
        _follow_up(ctx.topology, node.name, self.service, "recreate")
        return undo

    revert = staticmethod(_restore_file_and_follow)

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.service],
            "file": self.file,
            "applied_by": "recreate",
            "changes": [{"key": self.key, "before": getattr(self, "_before", None), "after": self.value}],
        }


@dataclass
class ManualEdit(Action):
    """Ручная правка файла на сервере «по SSH» в обход обычного процесса
    (compose.yaml, конфиг nginx) и её применение."""

    category: ClassVar[str] = "manual"
    change_related: ClassVar[bool] = True

    service: str
    file: str
    replacements: list[tuple[str, str]]
    follow_up: str | None = None
    # На каком узле править (по умолчанию — узел сервиса).
    on_node_of: str | None = None

    def touched_files(self) -> set[str]:
        return {self.file}

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.on_node_of or self.service)
        original = node.read_text(self.file)
        updated = original
        for old, new in self.replacements:
            if updated.count(old) != 1:
                raise ValueError(f"{self.file}: fragment not found exactly once: {old!r}")
            updated = updated.replace(old, new)
        undo = {
            "type": "ManualEdit",
            "node": node.name,
            "service": self.service,
            "file": self.file,
            "original": original,
            "follow_up": self.follow_up,
        }
        node.write_text(self.file, updated)
        _follow_up(ctx.topology, node.name, self.service, self.follow_up)
        return undo

    revert = staticmethod(_restore_file_and_follow)

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.on_node_of or self.service],
            "file": self.file,
            "applied_by": self.follow_up or "none",
            "actor": "ssh",
            "changes": [{"before": old.strip(), "after": new.strip()} for old, new in self.replacements],
        }


@dataclass
class Deploy(Action):
    """Выкатка новой версии .NET-сервиса: сборка образа, смена версии в .env,
    пересоздание контейнера. fault=None — обычный (безвредный) релиз."""

    category: ClassVar[str] = "deploy"
    change_related: ClassVar[bool] = True

    service: str
    fault: str | None = None

    def touched_files(self) -> set[str]:
        return {".env"}

    def prepare(self, ctx: Context) -> None:
        patch = ctx.state.get("next_patch", 1)
        ctx.state["next_patch"] = patch + 1
        self.version = f"1.0.{patch}"
        node = ctx.topology.node_of(self.service)
        project = DOTNET_PROJECTS[self.service]
        node.run(
            [
                "docker",
                "build",
                "-q",
                "-t",
                f"shop-{self.service}:{self.version}",
                "--build-arg",
                f"SERVICE={project}",
                "--build-arg",
                f"BUILD_FAULT={self.fault or 'none'}",
                "--build-arg",
                f"BUILD_ID={uuid.uuid4().hex}",
                node.app_dir,
            ],
            timeout=1200,
        )

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        original = node.read_text(".env")
        updated, self._before = set_env_line(original, VERSION_VARS[self.service], self.version)
        undo = {
            "type": "Deploy",
            "node": node.name,
            "service": self.service,
            "file": ".env",
            "original": original,
            "follow_up": "recreate",
        }
        node.write_text(".env", updated)
        _follow_up(ctx.topology, node.name, self.service, "recreate")
        return undo

    revert = staticmethod(_restore_file_and_follow)

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.service],
            "file": ".env",
            "changes": [
                {
                    "key": VERSION_VARS[self.service],
                    "before": getattr(self, "_before", None),
                    "after": getattr(self, "version", None),
                }
            ],
            "build_fault": self.fault or "none",
        }


# ---------- сбои без изменений конфигурации ----------


@dataclass
class PumbaNetem(Action):
    category: ClassVar[str] = "network"
    change_related: ClassVar[bool] = False

    service: str
    delay_ms: int = 0
    jitter_ms: int = 0
    loss_percent: int = 0

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        name = f"icc-fault-{ctx.incident_id}-netem"
        duration = f"{ctx.hold_seconds + 120}s"
        if self.loss_percent:
            emulation = ["loss", "--percent", str(self.loss_percent)]
        else:
            emulation = ["delay", "--time", str(self.delay_ms), "--jitter", str(self.jitter_ms)]
        undo = {"type": "PumbaNetem", "node": node.name, "container": name}
        node.run(
            [
                "docker",
                "run",
                "-d",
                "--name",
                name,
                "--label",
                INJECTOR_LABEL,
                "-v",
                "/var/run/docker.sock:/var/run/docker.sock",
                PUMBA_IMAGE,
                "--log-level",
                "warning",
                "netem",
                "--duration",
                duration,
                "--tc-image",
                NETTOOLS_IMAGE,
                *emulation,
                container_name(self.service),
            ]
        )
        return undo

    @staticmethod
    def revert(topology: Topology, undo: dict[str, Any]) -> None:
        node = topology.nodes[undo["node"]]
        # stop, а не rm -f: Pumba снимает правила tc только при корректном завершении.
        node.run(["docker", "stop", "-t", "20", undo["container"]], check=False)
        node.run(["docker", "rm", "-f", undo["container"]], check=False)
        _cleanup_helpers(topology, undo["node"])

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.service],
            "delay_ms": self.delay_ms,
            "jitter_ms": self.jitter_ms,
            "loss_percent": self.loss_percent,
        }


@dataclass
class PumbaStress(Action):
    """stress-ng внутри cgroup контейнера: CPU или память."""

    category: ClassVar[str] = "resource"
    change_related: ClassVar[bool] = False

    service: str
    stressors: str

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        name = f"icc-fault-{ctx.incident_id}-stress"
        seconds = ctx.hold_seconds + 120
        undo = {"type": "PumbaStress", "node": node.name, "container": name}
        node.run(
            [
                "docker",
                "run",
                "-d",
                "--name",
                name,
                "--label",
                INJECTOR_LABEL,
                "-v",
                "/var/run/docker.sock:/var/run/docker.sock",
                PUMBA_IMAGE,
                "--log-level",
                "warning",
                "stress",
                "--duration",
                f"{seconds}s",
                "--stress-image",
                STRESS_IMAGE,
                f"--stressors={self.stressors} --timeout {seconds}s",
                container_name(self.service),
            ]
        )
        return undo

    @staticmethod
    def revert(topology: Topology, undo: dict[str, Any]) -> None:
        node = topology.nodes[undo["node"]]
        node.run(["docker", "stop", "-t", "20", undo["container"]], check=False)
        node.run(["docker", "rm", "-f", undo["container"]], check=False)
        _cleanup_helpers(topology, undo["node"])

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.service],
            "stressors": self.stressors,
        }


@dataclass
class NodeCpuHog(Action):
    """«Шумный сосед»: процесс без лимитов нагружает CPU всего узла.
    Причина — узел, а не сервис; service — сервис, рядом с которым он запущен."""

    category: ClassVar[str] = "resource"
    change_related: ClassVar[bool] = False

    service: str
    workers: int = 0  # 0 — по числу ядер

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        name = f"icc-fault-{ctx.incident_id}-cpuhog"
        undo = {"type": "NodeCpuHog", "node": node.name, "container": name}
        node.run(
            [
                "docker",
                "run",
                "-d",
                "--name",
                name,
                "--label",
                INJECTOR_LABEL,
                STRESS_IMAGE,
                "--cpu",
                str(self.workers),
                "--timeout",
                f"{ctx.hold_seconds + 120}s",
            ]
        )
        return undo

    @staticmethod
    def revert(topology: Topology, undo: dict[str, Any]) -> None:
        topology.nodes[undo["node"]].run(["docker", "rm", "-f", undo["container"]], check=False)

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": None,
            "node": topology.services[self.service],
            "near_service": self.service,
            "workers": self.workers,
        }


@dataclass
class ContainerState(Action):
    """Падение или зависание зависимости: kill (как крах процесса) или pause."""

    category: ClassVar[str] = "dependency"
    change_related: ClassVar[bool] = False

    service: str
    operation: str  # kill | pause

    def apply(self, ctx: Context) -> dict[str, Any]:
        node = ctx.topology.node_of(self.service)
        undo = {"type": "ContainerState", "node": node.name, "service": self.service, "operation": self.operation}
        node.run(["docker", self.operation, container_name(self.service)])
        return undo

    @staticmethod
    def revert(topology: Topology, undo: dict[str, Any]) -> None:
        node = topology.nodes[undo["node"]]
        name = container_name(undo["service"])
        if undo["operation"] == "pause":
            node.run(["docker", "unpause", name], check=False)
        else:
            state = node.run(["docker", "inspect", "-f", "{{.State.Running}}", name], check=False).stdout.strip()
            if state != "true":
                node.run(["docker", "start", name])

    def describe(self, topology: Topology) -> dict[str, Any]:
        return {
            "category": self.category,
            "service": self.service,
            "node": topology.services[self.service],
            "operation": self.operation,
        }


ACTION_TYPES: dict[str, type[Action]] = {
    cls.__name__: cls
    for cls in (JsonConfigEdit, EnvFileEdit, ManualEdit, Deploy, PumbaNetem, PumbaStress, NodeCpuHog, ContainerState)
}


def revert(topology: Topology, undo: dict[str, Any]) -> None:
    ACTION_TYPES[undo["type"]].revert(topology, undo)


__all__ = [name for name in dir() if not name.startswith("_")] + ["CommandError"]
