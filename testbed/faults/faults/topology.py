"""Топология стенда: узлы, где какой сервис работает, и как выполнять на узле команды."""

from __future__ import annotations

import posixpath
import shlex
import subprocess
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

PROJECT = "shop"

# Сервисы магазина и их .NET-проекты (для сборки «плохих» версий).
DOTNET_PROJECTS = {
    "api": "Shop.Api",
    "catalog": "Shop.Catalog",
    "payments-worker": "Shop.PaymentsWorker",
    "notifications-worker": "Shop.NotificationsWorker",
}

# Переменная версии образа в .env для каждого .NET-сервиса.
VERSION_VARS = {
    "api": "API_VERSION",
    "catalog": "CATALOG_VERSION",
    "payments-worker": "PAYMENTS_VERSION",
    "notifications-worker": "NOTIFICATIONS_VERSION",
}


class CommandError(RuntimeError):
    pass


@dataclass
class Node:
    """Узел стенда. Без ssh — команды выполняются на этой машине."""

    name: str
    deploy_dir: str
    compose_files: list[str] = field(default_factory=lambda: ["compose.yaml"])
    ssh: str | None = None

    @property
    def is_remote(self) -> bool:
        return self.ssh is not None

    def path(self, relative: str) -> str:
        if self.is_remote:
            return posixpath.join(self.deploy_dir, relative)
        return str(Path(self.deploy_dir) / relative)

    @property
    def app_dir(self) -> str:
        return posixpath.dirname(self.deploy_dir) if self.is_remote else str(Path(self.deploy_dir).parent)

    def run(
        self,
        args: list[str],
        *,
        cwd: str | None = None,
        input: str | None = None,
        check: bool = True,
        timeout: float = 600,
    ) -> subprocess.CompletedProcess[str]:
        if self.is_remote:
            command = shlex.join(args)
            if cwd:
                command = f"cd {shlex.quote(cwd)} && {command}"
            full = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", self.ssh, command]
            result = subprocess.run(
                full, input=input, capture_output=True, text=True, encoding="utf-8", timeout=timeout
            )
        else:
            result = subprocess.run(
                args, cwd=cwd, input=input, capture_output=True, text=True, encoding="utf-8", timeout=timeout
            )
        if check and result.returncode != 0:
            raise CommandError(
                f"[{self.name}] {shlex.join(args)} → exit {result.returncode}: "
                f"{(result.stderr or result.stdout).strip()[-800:]}"
            )
        return result

    def compose(self, *args: str, check: bool = True, timeout: float = 600) -> subprocess.CompletedProcess[str]:
        files = [arg for f in self.compose_files for arg in ("-f", f)]
        return self.run(["docker", "compose", *files, *args], cwd=self.deploy_dir, check=check, timeout=timeout)

    def read_text(self, relative: str) -> str:
        if self.is_remote:
            return self.run(["cat", self.path(relative)]).stdout
        return Path(self.path(relative)).read_text(encoding="utf-8")

    def write_text(self, relative: str, content: str) -> None:
        if self.is_remote:
            self.run(["sh", "-c", f"cat > {shlex.quote(self.path(relative))}"], input=content)
        else:
            # newline="" — не превращать \n в \r\n на Windows: файлы стенда в LF.
            with open(self.path(relative), "w", encoding="utf-8", newline="") as f:
                f.write(content)


def container_name(service: str) -> str:
    return f"{PROJECT}-{service}-1"


@dataclass
class Topology:
    nodes: dict[str, Node]
    services: dict[str, str]
    gateway_url: str
    runs_dir: Path
    load_node: str | None = None
    source: Path | None = None

    def node_of(self, service: str) -> Node:
        try:
            return self.nodes[self.services[service]]
        except KeyError as e:
            raise KeyError(f"service {service!r} is not mapped to a node in topology") from e

    @classmethod
    def load(cls, path: Path) -> Topology:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
        base = path.parent
        nodes = {}
        for name, raw in data["nodes"].items():
            deploy_dir = raw["deploy_dir"]
            if "ssh" not in raw:
                deploy_dir = str((base / deploy_dir).resolve())
            nodes[name] = Node(
                name=name,
                deploy_dir=deploy_dir,
                compose_files=raw.get("compose_files", ["compose.yaml"]),
                ssh=raw.get("ssh"),
            )
        services = dict(data["services"])
        load_node = services.pop("load", None)
        for service, node in services.items():
            if node not in nodes:
                raise ValueError(f"service {service} → unknown node {node}")
        campaign = data.get("campaign", {})
        return cls(
            nodes=nodes,
            services=services,
            gateway_url=campaign.get("gateway_url", "http://localhost:8000"),
            runs_dir=(base / campaign.get("runs_dir", "../runs")).resolve(),
            load_node=load_node,
            source=path,
        )
