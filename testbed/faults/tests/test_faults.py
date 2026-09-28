import json
import re
import shutil
from collections import Counter
from pathlib import Path

import pytest

from faults import actions
from faults.actions import (
    Context,
    Deploy,
    EnvFileEdit,
    JsonConfigEdit,
    ManualEdit,
    get_nested,
    set_env_line,
    set_nested,
)
from faults.runner import build_plan
from faults.scenarios import CLASSES, DECOYS, SCENARIOS
from faults.topology import Node, Topology
from faults.verify import app_stats, container_changes, gateway_stats, signals_of

TESTBED = Path(__file__).resolve().parents[2]
DEPLOY = TESTBED / "app" / "deploy"
SHOP_SRC = TESTBED / "app" / "src"

ALL_ACTIONS = [(s.id, s.make()) for s in SCENARIOS] + [(d[0], d[2]()) for d in DECOYS]


@pytest.fixture
def topology(tmp_path, monkeypatch):
    deploy = tmp_path / "deploy"
    shutil.copytree(DEPLOY, deploy, ignore=shutil.ignore_patterns(".env"))
    shutil.copy(DEPLOY / ".env.example", deploy / ".env")
    node = Node(name="local", deploy_dir=str(deploy))
    services = {
        s: "local"
        for s in (
            "gateway",
            "api",
            "catalog",
            "rabbitmq",
            "payments-worker",
            "notifications-worker",
            "redis",
            "postgres",
        )
    }
    # Без docker: проверяем только работу с файлами.
    monkeypatch.setattr(actions, "_follow_up", lambda *args: None)
    return Topology(nodes={"local": node}, services=services, gateway_url="http://x", runs_dir=tmp_path / "runs")


def test_nested_helpers():
    data = {"A": {"B": 1}}
    set_nested(data, "A:C", 2)
    set_nested(data, "X:Y", 3)
    assert data == {"A": {"B": 1, "C": 2}, "X": {"Y": 3}}
    assert get_nested(data, "A:B") == 1
    assert get_nested(data, "A:Z") is None


def test_set_env_line_replaces_appends_removes():
    text = "# c\nA=1\nB=2\n"
    replaced, before = set_env_line(text, "A", "9")
    assert (replaced, before) == ("# c\nA=9\nB=2\n", "1")
    appended, before = set_env_line(text, "C", "3")
    assert (appended, before) == ("# c\nA=1\nB=2\nC=3\n", None)
    removed, before = set_env_line(text, "B", None)
    assert (removed, before) == ("# c\nA=1\n", "2")


@pytest.mark.parametrize(
    "action_id,action", [a for a in ALL_ACTIONS if isinstance(a[1], (JsonConfigEdit, EnvFileEdit, ManualEdit))]
)
def test_file_edit_applies_and_reverts_exactly(topology, action_id, action):
    node = topology.nodes["local"]
    rel = next(iter(action.touched_files()))
    original = node.read_text(rel)
    ctx = Context(topology=topology, incident_id="t", hold_seconds=1, state={})

    undo = action.apply(ctx)
    assert node.read_text(rel) != original, f"{action_id} did not change {rel}"
    json.dumps(undo)  # запись для отката сериализуема
    actions.revert(topology, undo)

    assert node.read_text(rel) == original


@pytest.mark.parametrize("action_id,action", [a for a in ALL_ACTIONS if isinstance(a[1], JsonConfigEdit)])
def test_json_edits_change_existing_or_known_keys(topology, action_id, action):
    """Ключи настроек должны существовать в классах опций сервиса, иначе правка ни на что не влияет."""
    options_source = "\n".join(p.read_text(encoding="utf-8") for p in SHOP_SRC.rglob("*.cs"))
    for key in action.values:
        prop = key.split(":")[-1]
        assert re.search(rf"public \w+\?? {prop} \{{ get; set; \}}", options_source), f"{action_id}: {key}"


def test_deploy_faults_exist_in_shop_code():
    source = "\n".join(p.read_text(encoding="utf-8") for p in SHOP_SRC.rglob("*.cs"))
    faults = {s.make().fault for s in SCENARIOS if isinstance(s.make(), Deploy)}
    assert faults
    for fault in faults:
        assert f'BuildFault.Is("{fault}")' in source, fault


def test_every_class_has_scenarios():
    counts = Counter(s.fault_class for s in SCENARIOS)
    assert set(counts) == set(CLASSES)
    assert all(n >= 3 for n in counts.values())


def test_plan_is_balanced_and_decoys_respect_files():
    plan = build_plan(per_class=20, seed=1, decoy_change_prob=0.0, decoy_other_prob=1.0)
    by_class = Counter(next(s.fault_class for s in SCENARIOS if s.id == p["scenario"]) for p in plan)
    assert by_class == {c: 20 for c in CLASSES}
    decoys = {d[0]: d[2]() for d in DECOYS}
    for item in plan:
        scenario = next(s for s in SCENARIOS if s.id == item["scenario"])
        if scenario.change_related:
            assert item["decoy"] is None
        else:
            assert item["decoy"] is not None
            assert not decoys[item["decoy"]].touched_files() & scenario.make().touched_files()
    assert build_plan(20, 1, 0.0, 1.0) == plan  # воспроизводимость по seed


def test_gateway_and_app_stats():
    lines = [
        '1.2.3.4 [t] "GET / HTTP/1.1" 200 10 rt=0.010 urt=0.009 us=200',
        '1.2.3.4 [t] "GET / HTTP/1.1" 503 10 rt=0.500 urt=0.499 us=503',
        '1.2.3.4 [t] "GET / HTTP/1.1" 404 10 rt=0.020 urt=0.019 us=404',
        '1.2.3.4 [t] "GET / HTTP/1.1" 200 10 rt=0.030 urt=0.029 us=200',
    ]
    stats = gateway_stats(lines)
    assert stats["requests"] == 4
    assert stats["error_5xx_rate"] == 0.25
    assert stats["error_4xx_rate"] == 0.25
    assert app_stats(
        [
            "2026-09-28T10:00:00.000Z warn: X[0] slow",
            "2026-09-28T10:00:00.000Z fail: X[0] boom",
            "2026-09-28T10:00:00.000Z info: X[0] ok",
        ]
    ) == {"warn": 1, "error": 1}


def test_container_changes_detects_restarts_oom_down_and_recreate():
    before = {
        "api": {"id": "a1", "restarts": 0, "oom_killed": False, "up": True},
        "redis": {"id": "r1", "restarts": 0, "oom_killed": False, "up": True},
        "catalog": {"id": "c1", "restarts": 0, "oom_killed": False, "up": True},
        "postgres": {"id": "p1", "restarts": 0, "oom_killed": False, "up": True},
    }
    after = {
        "api": {"id": "a1", "restarts": 3, "oom_killed": True, "up": True},
        "redis": {"id": "r1", "restarts": 0, "oom_killed": False, "up": False},
        "catalog": {"id": "c2", "restarts": 0, "oom_killed": False, "up": True},
        "postgres": {"id": "p1", "restarts": 0, "oom_killed": False, "up": True},
    }
    assert container_changes(before, after) == {
        "api": {"restarts": 3, "oom_killed": True},
        "redis": {"down": True},
        "catalog": {"recreated": True},
    }


def test_container_changes_detects_memory_pressure_onset():
    before = {
        "worker": {"id": "w1", "restarts": 0, "oom_killed": False, "up": True, "memory_ratio": 0.2},
        "db": {"id": "d1", "restarts": 0, "oom_killed": False, "up": True, "memory_ratio": 0.95},
    }
    after = {
        "worker": {"id": "w1", "restarts": 0, "oom_killed": False, "up": True, "memory_ratio": 0.97},
        "db": {"id": "d1", "restarts": 0, "oom_killed": False, "up": True, "memory_ratio": 0.96},
    }
    # Память, которая уже была высокой до сбоя, симптомом не считается.
    assert container_changes(before, after) == {"worker": {"memory_high": 0.97}}


def test_signals_of_combines_all_sources():
    report = {
        "gateway": {
            "baseline": {"requests": 100, "error_5xx_rate": 0.0, "error_4xx_rate": 0.0, "p95_seconds": 0.01},
            "fault": {"requests": 100, "error_5xx_rate": 0.3, "error_4xx_rate": 0.0, "p95_seconds": 0.5},
        },
        "logs": {
            "payments-worker": {"baseline_scaled": {"warn": 1, "error": 0}, "fault": {"warn": 1, "error": 50}},
            "catalog": {"baseline_scaled": {"warn": 0, "error": 0}, "fault": {"warn": 3, "error": 0}},
        },
        "containers": {"api": {"restarts": 2}, "catalog": {"recreated": True}},
        "queues": {
            "payments.order-created": {"at_injection": 0, "at_end": 500},
            "shop.dead-letters": {"at_injection": 0, "at_end": 3},
        },
    }
    assert signals_of(report) == [
        "gateway_5xx",
        "gateway_latency",
        "payments-worker_log_errors",
        "api_restarts",
        "queue_payments.order-created",
    ]
