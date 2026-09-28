"""Жизненный цикл инцидента, проверка здоровья стенда, разметка и кампании."""

from __future__ import annotations

import json
import random
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import actions, verify
from .actions import Action, Context
from .scenarios import CLASSES, DECOYS, SCENARIOS, Scenario, by_id, decoy_by_id
from .topology import PROJECT, Topology, container_name

INJECTOR_VERSION = "1.0.0"

# Файлы, которые может менять инжектор: их эталонные копии сохраняются
# перед кампанией, чтобы стенд всегда можно было вернуть в исходное состояние.
PRISTINE_FILES = [
    ".env",
    "compose.yaml",
    "gateway/nginx.conf",
    *[f"config/{s}/appsettings.json" for s in ("api", "catalog", "payments-worker", "notifications-worker")],
    *[f"env/{s}.env" for s in ("api", "catalog", "payments-worker", "notifications-worker")],
]

BACKLOG_LIMIT = 200


def now() -> datetime:
    return datetime.now(UTC)


def iso(ts: datetime) -> str:
    return ts.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def log(message: str) -> None:
    print(f"{iso(now())} {message}", flush=True)


@dataclass
class Timings:
    decoy_lead: tuple[int, int] = (60, 240)
    hold: int = 480
    # Наблюдение за здоровым стендом перед приманкой/сбоем: чистое окно для сравнения.
    baseline: int = 300
    healthy_timeout: int = 600


# ---------- здоровье стенда ----------


@dataclass
class Health:
    ok: bool
    problems: list[str] = field(default_factory=list)


def check_health(topology: Topology) -> Health:
    problems: list[str] = []

    for node in topology.nodes.values():
        expected = {s for s, n in topology.services.items() if n == node.name}
        out = node.run(
            [
                "docker",
                "ps",
                "-a",
                "--filter",
                f"label=com.docker.compose.project={PROJECT}",
                "--format",
                '{{.Label "com.docker.compose.service"}}|{{.State}}|{{.Status}}',
            ],
            check=False,
        ).stdout
        seen = {}
        for line in out.splitlines():
            service, state, status = (line.split("|") + ["", ""])[:3]
            seen[service] = (state, status)
        for service in sorted(expected):
            state, status = seen.get(service, ("missing", ""))
            if state != "running" or "(healthy)" not in status:
                problems.append(f"{service}: {state} {status}".strip())
        leftovers = node.run(
            ["docker", "ps", "-aq", "--filter", f"label={actions.INJECTOR_LABEL}"], check=False
        ).stdout.split()
        if leftovers:
            problems.append(f"{node.name}: {len(leftovers)} injector container(s) left")

    try:
        with urllib.request.urlopen(f"{topology.gateway_url}/products/1", timeout=3) as r:
            if r.status != 200:
                problems.append(f"gateway: HTTP {r.status}")
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        problems.append(f"gateway: {e}")

    if "rabbitmq" in topology.services:
        node = topology.node_of("rabbitmq")
        out = node.run(
            ["docker", "exec", container_name("rabbitmq"), "rabbitmqctl", "list_queues", "-q", "name", "messages"],
            check=False,
        ).stdout
        for line in out.splitlines():
            parts = line.split()
            if (
                len(parts) == 2
                and parts[1].isdigit()
                and parts[0] != "shop.dead-letters"
                and int(parts[1]) > BACKLOG_LIMIT
            ):
                problems.append(f"queue {parts[0]}: {parts[1]} messages")

    return Health(ok=not problems, problems=problems)


def load_running(topology: Topology) -> bool | None:
    if topology.load_node is None:
        return None
    node = topology.nodes[topology.load_node]
    out = node.run(["docker", "ps", "--filter", "name=shop-load-k6", "--format", "{{.Names}}"], check=False)
    return bool(out.stdout.strip())


def wait_healthy(topology: Topology, timeout: int) -> Health:
    deadline = time.monotonic() + timeout
    health = check_health(topology)
    while not health.ok and time.monotonic() < deadline:
        time.sleep(10)
        health = check_health(topology)
    return health


def purge_dead_letters(topology: Topology) -> None:
    if "rabbitmq" in topology.services:
        topology.node_of("rabbitmq").run(
            ["docker", "exec", container_name("rabbitmq"), "rabbitmqctl", "purge_queue", "-q", "shop.dead-letters"],
            check=False,
        )


# ---------- состояние: эталонные файлы, незавершённый инцидент ----------


class State:
    def __init__(self, topology: Topology):
        self.topology = topology
        self.dir = topology.runs_dir
        self.dir.mkdir(parents=True, exist_ok=True)
        self.state_file = self.dir / "state.json"
        self.current_file = self.dir / "current.json"

    def load(self) -> dict[str, Any]:
        if self.state_file.exists():
            return json.loads(self.state_file.read_text(encoding="utf-8"))
        return {}

    def save(self, state: dict[str, Any]) -> None:
        self.state_file.write_text(json.dumps(state, indent=2), encoding="utf-8")

    def save_current(self, current: dict[str, Any]) -> None:
        tmp = self.current_file.with_suffix(".tmp")
        tmp.write_text(json.dumps(current, indent=2, ensure_ascii=False), encoding="utf-8")
        tmp.replace(self.current_file)

    def load_current(self) -> dict[str, Any] | None:
        if self.current_file.exists():
            return json.loads(self.current_file.read_text(encoding="utf-8"))
        return None

    def clear_current(self) -> None:
        self.current_file.unlink(missing_ok=True)

    def pristine_dir(self, node_name: str) -> Path:
        return self.dir / "pristine" / node_name

    def snapshot_pristine(self) -> None:
        """Сохраняет эталон, только если его ещё нет: эталон снимается со здорового стенда."""
        for node in self.topology.nodes.values():
            target = self.pristine_dir(node.name)
            if target.exists():
                continue
            for rel in PRISTINE_FILES:
                try:
                    content = node.read_text(rel)
                except (OSError, actions.CommandError):
                    continue
                path = target / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8", newline="")
            log(f"pristine snapshot saved for {node.name}")

    def restore_pristine(self) -> None:
        for node in self.topology.nodes.values():
            base = self.pristine_dir(node.name)
            if not base.exists():
                raise RuntimeError(f"no pristine snapshot for {node.name}")
            for path in base.rglob("*"):
                if path.is_file():
                    node.write_text(path.relative_to(base).as_posix(), path.read_text(encoding="utf-8"))
            ids = node.run(
                ["docker", "ps", "-aq", "--filter", f"label={actions.INJECTOR_LABEL}"], check=False
            ).stdout.split()
            if ids:
                node.run(["docker", "stop", "-t", "20", *ids], check=False)
                node.run(["docker", "rm", "-f", *ids], check=False)
            node.run(
                [
                    "docker",
                    "unpause",
                    *[container_name(s) for s, n in self.topology.services.items() if n == node.name],
                ],
                check=False,
            )
            node.compose("up", "-d", "--no-build", "--force-recreate", timeout=900)
            log(f"{node.name}: files restored from pristine snapshot, services recreated")


def revert_all(topology: Topology, undo_stack: list[dict[str, Any]]) -> list[str]:
    errors = []
    for undo in reversed(undo_stack):
        try:
            actions.revert(topology, undo)
        except Exception as e:  # откатываем всё, что можно, даже если один шаг упал
            errors.append(f"{undo['type']}: {e}")
            log(f"revert failed: {undo['type']}: {e}")
    return errors


# ---------- инцидент ----------


def choose_decoy(scenario: Scenario, fault: Action, rng: random.Random) -> str:
    """Приманка не трогает файлы сбоя; в половине случаев — тот же сервис (трудный случай)."""
    compatible = []
    for decoy_id, _, make in DECOYS:
        action = make()
        if action.touched_files() & fault.touched_files():
            continue
        compatible.append((decoy_id, action.service))
    same = [d for d, s in compatible if s == fault.service]
    pool = same if same and rng.random() < 0.5 else [d for d, _ in compatible]
    return rng.choice(pool)


def run_incident(
    topology: Topology,
    scenario: Scenario,
    *,
    decoy_id: str | None,
    timings: Timings,
    rng: random.Random,
    campaign: str,
    labels_file: Path,
    extra: dict[str, Any] | None = None,
) -> dict[str, Any]:
    store = State(topology)
    if store.load_current():
        raise RuntimeError("an unfinished incident exists: run `restore` first")

    health = wait_healthy(topology, timings.healthy_timeout)
    if not health.ok:
        raise RuntimeError(f"testbed is not healthy: {health.problems}")
    if load_running(topology) is False:
        log("WARNING: load generator is not running")

    incident_id = f"{now():%Y%m%d-%H%M%S}-{scenario.id}"
    state = store.load()
    ctx = Context(topology=topology, incident_id=incident_id.lower(), hold_seconds=timings.hold, state=state)

    fault = scenario.make()
    decoy = decoy_by_id(decoy_id)[2]() if decoy_id else None
    for action in (decoy, fault):
        if action is not None:
            action.prepare(ctx)
    store.save(state)

    timeline: dict[str, str] = {"baseline_from": iso(now())}
    log(f"[{incident_id}] baseline {timings.baseline}s")
    time.sleep(timings.baseline)

    undo_stack: list[dict[str, Any]] = []
    current = {"incident": incident_id, "undo": undo_stack}
    decoy_lead = rng.randint(*timings.decoy_lead) if decoy else 0
    errors: list[str] = []
    observed: dict[str, Any] | None = None
    log(f"[{incident_id}] {scenario.id} {scenario.description}" + (f" | decoy {decoy_id}" if decoy else ""))

    try:
        if decoy is not None:
            store.save_current(current)
            undo_stack.append(decoy.apply(ctx))
            store.save_current(current)
            timeline["decoy_at"] = iso(now())
            log(f"[{incident_id}] decoy applied, fault in {decoy_lead}s")
            time.sleep(decoy_lead)

        store.save_current(current)
        timeline["fault_started_at"] = iso(now())
        undo_stack.append(fault.apply(ctx))
        store.save_current(current)
        injected = now()
        timeline["injected_at"] = iso(injected)
        containers_at_injection = verify.container_snapshot(topology)
        queues_at_injection = verify.queue_depths(topology)
        log(f"[{incident_id}] fault injected, holding {timings.hold}s")
        time.sleep(timings.hold)

        # Симптомы снимаются до отката: пересоздание контейнера уничтожает его логи.
        hold_end = now()
        timeline["hold_ended_at"] = iso(hold_end)
        baseline_end = verify.parse_ts(timeline.get("decoy_at") or timeline["fault_started_at"])
        try:
            observed = verify.observe(
                topology,
                baseline=(verify.parse_ts(timeline["baseline_from"]), baseline_end),
                fault=(injected, hold_end),
                containers_at_injection=containers_at_injection,
                queues_at_injection=queues_at_injection,
            )
            log(f"[{incident_id}] observed: {', '.join(observed['signals']) or 'NO VISIBLE SYMPTOMS'}")
        except Exception as e:  # наблюдение не должно мешать откату
            errors.append(f"observe: {e}")
    finally:
        timeline["recovery_started_at"] = iso(now())
        errors += revert_all(topology, undo_stack)
        purge_dead_letters(topology)
        timeline["recovered_at"] = iso(now())
        store.clear_current()
        log(f"[{incident_id}] reverted")

    health = wait_healthy(topology, timings.healthy_timeout)
    timeline["healthy_at"] = iso(now())
    if not health.ok:
        errors.append(f"not healthy after recovery: {health.problems}")

    root_cause = fault.describe(topology)
    if root_cause.get("service"):
        root_cause["container"] = container_name(root_cause["service"])
    label = {
        "id": incident_id,
        "campaign": campaign,
        "scenario": scenario.id,
        "class": scenario.fault_class,
        "change_related": scenario.change_related,
        "description": scenario.description,
        "root_cause": root_cause,
        "decoy": (
            {"id": decoy_id, "description": decoy_by_id(decoy_id)[1], **decoy.describe(topology)} if decoy else None
        ),
        "timeline": timeline,
        "expected_symptoms": list(scenario.expected_symptoms),
        "params": {
            "hold_seconds": timings.hold,
            "baseline_seconds": timings.baseline,
            "decoy_lead_seconds": decoy_lead,
        },
        "observed": observed,
        "outcome": {"recovered_healthy": health.ok, "errors": errors},
        "injector_version": INJECTOR_VERSION,
        **(extra or {}),
    }
    labels_file.parent.mkdir(parents=True, exist_ok=True)
    with labels_file.open("a", encoding="utf-8") as f:
        f.write(json.dumps(label, ensure_ascii=False) + "\n")
    log(f"[{incident_id}] labeled → {labels_file}")
    return label


# ---------- кампания ----------


def build_plan(per_class: int, seed: int, decoy_change_prob: float, decoy_other_prob: float) -> list[dict[str, Any]]:
    rng = random.Random(seed)
    plan = []
    for fault_class in CLASSES:
        scenarios = [s for s in SCENARIOS if s.fault_class == fault_class]
        for i in range(per_class):
            scenario = scenarios[i % len(scenarios)]
            prob = decoy_change_prob if scenario.change_related else decoy_other_prob
            decoy = choose_decoy(scenario, scenario.make(), rng) if rng.random() < prob else None
            plan.append({"scenario": scenario.id, "decoy": decoy})
    rng.shuffle(plan)
    for seq, item in enumerate(plan):
        item["seq"] = seq
    return plan


def run_campaign(
    topology: Topology,
    name: str,
    *,
    per_class: int,
    seed: int,
    timings: Timings,
    decoy_change_prob: float,
    decoy_other_prob: float,
) -> None:
    campaign_dir = topology.runs_dir / name
    campaign_dir.mkdir(parents=True, exist_ok=True)
    plan_file = campaign_dir / "plan.json"
    labels_file = campaign_dir / "incidents.jsonl"

    if plan_file.exists():
        plan = json.loads(plan_file.read_text(encoding="utf-8"))
        log(f"resuming campaign {name}")
    else:
        plan = build_plan(per_class, seed, decoy_change_prob, decoy_other_prob)
        plan_file.write_text(json.dumps(plan, indent=2), encoding="utf-8")

    done = set()
    if labels_file.exists():
        for line in labels_file.read_text(encoding="utf-8").splitlines():
            done.add(json.loads(line).get("seq"))

    State(topology).snapshot_pristine()
    rng = random.Random(seed + 1)
    for item in plan:
        if item["seq"] in done:
            continue
        log(f"campaign {name}: {item['seq'] + 1}/{len(plan)}")
        run_incident(
            topology,
            by_id(item["scenario"]),
            decoy_id=item["decoy"],
            timings=timings,
            rng=rng,
            campaign=name,
            labels_file=labels_file,
            extra={"seq": item["seq"]},
        )
    log(f"campaign {name} complete: {len(plan)} incidents")
