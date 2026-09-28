"""Инжектор сбоев стенда ICC.

python -m faults list
python -m faults status
python -m faults inject M2 [--decoy X1] [--hold 480]
python -m faults campaign night-01 [--per-class 20] [--seed 42]
python -m faults restore [--pristine]
python -m faults verify runs/manual/incidents.jsonl [--last 1]
"""

from __future__ import annotations

import argparse
import json
import random
import sys
from pathlib import Path

from .runner import State, Timings, check_health, load_running, log, revert_all, run_campaign, run_incident
from .scenarios import CLASSES, DECOYS, SCENARIOS, by_id
from .topology import Topology
from .verify import summarize

DEFAULT_TOPOLOGY = Path(__file__).resolve().parent.parent / "topology.local.toml"


def main(argv: list[str] | None = None) -> int:
    # Консоль Windows по умолчанию не в UTF-8.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(
        prog="faults", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--topology", type=Path, default=DEFAULT_TOPOLOGY)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("list", help="сценарии и приманки")
    sub.add_parser("status", help="здоровье стенда")

    def add_timings(p: argparse.ArgumentParser) -> None:
        p.add_argument("--hold", type=int, default=480, help="сколько секунд держать сбой")
        p.add_argument("--baseline", type=int, default=300, help="наблюдение здорового стенда перед сбоем, с")
        p.add_argument(
            "--decoy-lead",
            type=int,
            nargs=2,
            default=(60, 240),
            metavar=("MIN", "MAX"),
            help="за сколько секунд до сбоя делать приманку",
        )

    inject = sub.add_parser("inject", help="один инцидент")
    inject.add_argument("scenario")
    inject.add_argument("--decoy", help="id приманки (X1…X8)")
    inject.add_argument("--labels", type=Path, help="куда дописать разметку (по умолчанию runs/manual/incidents.jsonl)")
    add_timings(inject)

    campaign = sub.add_parser("campaign", help="серия инцидентов (возобновляется после остановки)")
    campaign.add_argument("name")
    campaign.add_argument("--per-class", type=int, default=20)
    campaign.add_argument("--seed", type=int, default=42)
    campaign.add_argument(
        "--decoy-change-prob", type=float, default=0.0, help="доля приманок в сбоях, связанных с изменением"
    )
    campaign.add_argument(
        "--decoy-other-prob", type=float, default=1.0, help="доля приманок в сбоях, не связанных с изменением"
    )
    add_timings(campaign)

    restore = sub.add_parser("restore", help="откатить незавершённый инцидент")
    restore.add_argument("--pristine", action="store_true", help="вернуть все файлы к эталону и пересоздать сервисы")

    verify = sub.add_parser("verify", help="сводка наблюдённых симптомов по разметке")
    verify.add_argument("labels", type=Path)
    verify.add_argument("--last", type=int, help="только последние N инцидентов")

    args = parser.parse_args(argv)

    if args.command == "list":
        for fault_class, change_related in CLASSES.items():
            print(f"\n{fault_class} ({'связан с изменением' if change_related else 'без изменения'})")
            for s in SCENARIOS:
                if s.fault_class == fault_class:
                    print(f"  {s.id:4} {s.description}")
        print("\nприманки")
        for decoy_id, description, _ in DECOYS:
            print(f"  {decoy_id:4} {description}")
        return 0

    topology = Topology.load(args.topology)

    if args.command == "status":
        health = check_health(topology)
        print(
            json.dumps(
                {
                    "healthy": health.ok,
                    "problems": health.problems,
                    "load_running": load_running(topology),
                    "unfinished_incident": State(topology).load_current() is not None,
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        return 0 if health.ok else 1

    if args.command == "verify":
        reports = summarize(args.labels, args.last)
        print(json.dumps(reports, ensure_ascii=False, indent=2))
        return 0 if all(r["visible"] for r in reports) else 1

    if args.command == "restore":
        store = State(topology)
        current = store.load_current()
        if current:
            log(f"reverting unfinished incident {current['incident']}")
            errors = revert_all(topology, current["undo"])
            store.clear_current()
            if errors:
                log(f"errors: {errors}")
        if args.pristine:
            store.restore_pristine()
        if not current and not args.pristine:
            log("nothing to restore")
        return 0

    timings = Timings(decoy_lead=tuple(args.decoy_lead), hold=args.hold, baseline=args.baseline)

    if args.command == "inject":
        State(topology).snapshot_pristine()
        labels = args.labels or topology.runs_dir / "manual" / "incidents.jsonl"
        run_incident(
            topology,
            by_id(args.scenario),
            decoy_id=args.decoy,
            timings=timings,
            rng=random.Random(),
            campaign="manual",
            labels_file=labels,
        )
        return 0

    if args.command == "campaign":
        run_campaign(
            topology,
            args.name,
            per_class=args.per_class,
            seed=args.seed,
            timings=timings,
            decoy_change_prob=args.decoy_change_prob,
            decoy_other_prob=args.decoy_other_prob,
        )
        return 0

    return 2


if __name__ == "__main__":
    raise SystemExit(main())
