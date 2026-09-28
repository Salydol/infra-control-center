"""Наблюдение симптомов инцидента.

Снимается до отката, пока контейнеры сбоя ещё живы: при пересоздании
контейнера Docker удаляет его логи, а буфер `docker events` хранит лишь
несколько минут. Окно сбоя [injected_at, конец удержания] сравнивается
с базовым окном перед приманкой или сбоем. Результат пишется в разметку
(`observed`) и нужен, чтобы отбраковать «тихие» инциденты и откалибровать
сценарии.
"""

from __future__ import annotations

import json
import re
from datetime import datetime
from pathlib import Path
from typing import Any

from .topology import PROJECT, Topology, container_name

GATEWAY_LINE = re.compile(r'" (?P<status>\d{3}) \d+ rt=(?P<rt>[\d.]+)')
APP_LEVEL = re.compile(r"^\S+ (?P<level>trce|dbug|info|warn|fail|crit):")
DEAD_LETTERS = "shop.dead-letters"


def parse_ts(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def _docker_ts(ts: datetime) -> str:
    return ts.strftime("%Y-%m-%dT%H:%M:%SZ")


def _logs(topology: Topology, service: str, start: datetime, end: datetime) -> list[str]:
    node = topology.node_of(service)
    out = node.run(
        ["docker", "logs", "--since", _docker_ts(start), "--until", _docker_ts(end), container_name(service)],
        check=False,
    )
    return (out.stdout + out.stderr).splitlines()


def gateway_stats(lines: list[str]) -> dict[str, Any]:
    statuses: list[int] = []
    times: list[float] = []
    for line in lines:
        m = GATEWAY_LINE.search(line)
        if m:
            statuses.append(int(m["status"]))
            times.append(float(m["rt"]))
    n = len(statuses)
    times.sort()
    return {
        "requests": n,
        "error_5xx_rate": round(sum(s >= 500 for s in statuses) / n, 4) if n else None,
        "error_4xx_rate": round(sum(400 <= s < 500 for s in statuses) / n, 4) if n else None,
        "p95_seconds": times[int(0.95 * (n - 1))] if n else None,
    }


def app_stats(lines: list[str]) -> dict[str, int]:
    counts = {"warn": 0, "error": 0}
    for line in lines:
        m = APP_LEVEL.match(line)
        if m:
            level = m["level"]
        elif re.search(r"\b(FATAL|ERROR|error)\b", line):
            level = "error"
        elif re.search(r"\b(WARNING|warning)\b", line):
            level = "warn"
        else:
            level = None
        if level == "warn":
            counts["warn"] += 1
        elif level in ("fail", "crit", "error"):
            counts["error"] += 1
    return counts


def container_snapshot(topology: Topology) -> dict[str, dict[str, Any]]:
    """Состояние контейнеров магазина: id, число перезапусков, OOM, работает ли."""
    result: dict[str, dict[str, Any]] = {}
    fmt = (
        '{{index .Config.Labels "com.docker.compose.service"}}|{{.Id}}|{{.RestartCount}}|'
        "{{.State.OOMKilled}}|{{.State.Running}}|{{.State.Paused}}"
    )
    for node in topology.nodes.values():
        ids = node.run(
            ["docker", "ps", "-aq", "--filter", f"label=com.docker.compose.project={PROJECT}"], check=False
        ).stdout.split()
        if not ids:
            continue
        out = node.run(["docker", "inspect", "-f", fmt, *ids], check=False).stdout
        for line in out.splitlines():
            parts = line.split("|")
            if len(parts) != 6:
                continue
            service, cid, restarts, oom, running, paused = parts
            result[service] = {
                "id": cid[:12],
                "restarts": int(restarts or 0),
                "oom_killed": oom == "true",
                "up": running == "true" and paused != "true",
            }
        # Доля лимита памяти: давление по памяти видно раньше, чем OOM.
        stats = node.run(
            ["docker", "stats", "--no-stream", "--format", "{{.Name}}|{{.MemPerc}}", *ids], check=False
        ).stdout
        for line in stats.splitlines():
            name, _, percent = line.partition("|")
            service = name.removeprefix(f"{PROJECT}-").rsplit("-", 1)[0]
            if service in result and percent.strip().endswith("%"):
                try:
                    result[service]["memory_ratio"] = round(float(percent.strip()[:-1]) / 100, 3)
                except ValueError:
                    pass
    return result


def container_changes(before: dict[str, Any], after: dict[str, Any]) -> dict[str, dict[str, Any]]:
    changes: dict[str, dict[str, Any]] = {}
    for service, now in after.items():
        was = before.get(service)
        change: dict[str, Any] = {}
        if was and was["id"] == now["id"] and now["restarts"] > was["restarts"]:
            change["restarts"] = now["restarts"] - was["restarts"]
        if was and was["id"] != now["id"]:
            change["recreated"] = True
        if now["oom_killed"]:
            change["oom_killed"] = True
        if not now["up"]:
            change["down"] = True
        memory = now.get("memory_ratio") or 0
        if memory >= 0.9 and (not was or (was.get("memory_ratio") or 0) < 0.9):
            change["memory_high"] = memory
        if change:
            changes[service] = change
    return changes


def queue_depths(topology: Topology) -> dict[str, int]:
    if "rabbitmq" not in topology.services:
        return {}
    node = topology.node_of("rabbitmq")
    out = node.run(
        ["docker", "exec", container_name("rabbitmq"), "rabbitmqctl", "list_queues", "-q", "name", "messages"],
        check=False,
        timeout=60,
    ).stdout
    depths = {}
    for line in out.splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[1].isdigit():
            depths[parts[0]] = int(parts[1])
    return depths


def observe(
    topology: Topology,
    baseline: tuple[datetime, datetime],
    fault: tuple[datetime, datetime],
    containers_at_injection: dict[str, Any],
    queues_at_injection: dict[str, int],
) -> dict[str, Any]:
    report: dict[str, Any] = {
        "baseline_seconds": int((baseline[1] - baseline[0]).total_seconds()),
        "fault_seconds": int((fault[1] - fault[0]).total_seconds()),
    }

    gw_base = gateway_stats(_logs(topology, "gateway", *baseline))
    gw_fault = gateway_stats(_logs(topology, "gateway", *fault))
    report["gateway"] = {"baseline": gw_base, "fault": gw_fault}

    # Окна могут быть разной длины: базовые счётчики приводятся к длине окна сбоя.
    scale = max(report["fault_seconds"], 1) / max(report["baseline_seconds"], 1)
    report["logs"] = {}
    for service in topology.services:
        if service == "gateway":
            continue
        base = app_stats(_logs(topology, service, *baseline))
        now = app_stats(_logs(topology, service, *fault))
        expected = {k: round(v * scale) for k, v in base.items()}
        if now != expected:
            report["logs"][service] = {"baseline_scaled": expected, "fault": now}

    report["containers"] = container_changes(containers_at_injection, container_snapshot(topology))
    queues_end = queue_depths(topology)
    report["queues"] = {
        q: {"at_injection": queues_at_injection.get(q, 0), "at_end": depth}
        for q, depth in queues_end.items()
        if depth != queues_at_injection.get(q, 0)
    }
    report["signals"] = signals_of(report)
    report["visible"] = bool(report["signals"])
    return report


def signals_of(report: dict[str, Any]) -> list[str]:
    signals = []
    gw_base, gw_fault = report["gateway"]["baseline"], report["gateway"]["fault"]
    if gw_base["requests"] and gw_fault["requests"]:
        if (gw_fault["error_5xx_rate"] or 0) - (gw_base["error_5xx_rate"] or 0) > 0.02:
            signals.append("gateway_5xx")
        if (gw_fault["error_4xx_rate"] or 0) - (gw_base["error_4xx_rate"] or 0) > 0.05:
            signals.append("gateway_4xx")
        if gw_base["p95_seconds"] is not None and gw_fault["p95_seconds"] > 2 * gw_base["p95_seconds"] + 0.02:
            signals.append("gateway_latency")
    elif gw_base["requests"] and not gw_fault["requests"]:
        signals.append("gateway_no_traffic")
    for service, stats in report["logs"].items():
        if stats["fault"]["error"] > stats["baseline_scaled"]["error"] + 5:
            signals.append(f"{service}_log_errors")
        elif stats["fault"]["warn"] > stats["baseline_scaled"]["warn"] + 20:
            signals.append(f"{service}_log_warnings")
    for service, change in report["containers"].items():
        for key in ("restarts", "oom_killed", "down", "memory_high"):
            if change.get(key):
                signals.append(f"{service}_{key}")
    for queue, q in report["queues"].items():
        grew = q["at_end"] - q["at_injection"]
        if (queue == DEAD_LETTERS and grew > 10) or grew > 100:
            signals.append(f"queue_{queue}")
    return signals


def summarize(labels_file: Path, last: int | None) -> list[dict[str, Any]]:
    labels = [json.loads(line) for line in labels_file.read_text(encoding="utf-8").splitlines() if line.strip()]
    if last:
        labels = labels[-last:]
    return [
        {
            "id": label["id"],
            "scenario": label["scenario"],
            "visible": (label.get("observed") or {}).get("visible"),
            "signals": (label.get("observed") or {}).get("signals"),
            "errors": label["outcome"]["errors"],
        }
        for label in labels
    ]
