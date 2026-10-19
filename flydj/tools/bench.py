#!/usr/bin/env python3
"""Measure simulation cost on the local machine.

    python tools/bench.py [artifact.npz] [bio_seconds]

Prints CPU milliseconds per biological second, the resulting real-time factor
(biological seconds simulated per CPU second) and the resident set size.
"""

from __future__ import annotations

import resource
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from app.config import DEFAULT_MODEL_PATH  # noqa: E402
from app.connectome import load_connectome  # noqa: E402
from app.simulator import Graph, Simulator, warm_up  # noqa: E402


def main() -> None:
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_MODEL_PATH
    bio_s = float(sys.argv[2]) if len(sys.argv) > 2 else 5.0
    started = time.time()
    model = load_connectome(path)
    print(f"loaded {path.name}: {model.n_neurons} neurons, {model.n_connections} connections in {time.time() - started:.2f} s")
    warm_up()
    sim = Simulator(Graph(model.indptr, model.indices, model.weights), {k: model.population_indices(k) for k in model.input_keys()})
    cases = {
        "ambient 2 Hz": {k: 2.0 for k in model.input_keys()},
        "typical music": {"jo_a": 40.0, "jo_b": 90.0, "jo_ce": 120.0},
        "maximum 200 Hz": {k: 200.0 for k in model.input_keys()},
    }
    for name, rates in cases.items():
        sim.reset(1)
        sim.set_rates(rates)
        cpu0, wall0 = time.thread_time(), time.perf_counter()
        spikes = 0
        for _ in range(int(bio_s * 1000 / 50)):
            spikes += sim.run_ms(50).count
        cpu, wall = time.thread_time() - cpu0, time.perf_counter() - wall0
        print(
            f"{name:16s} {1000 * cpu / bio_s:7.1f} cpu-ms per bio-s   real-time factor {bio_s / wall:5.1f}x   "
            f"{spikes / bio_s:9.0f} spikes/s"
        )
    rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    print(f"peak RSS {rss / (1e6 if sys.platform == 'darwin' else 1e3):.0f} MB")


if __name__ == "__main__":
    main()
