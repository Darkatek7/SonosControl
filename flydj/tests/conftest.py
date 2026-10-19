"""Shared fixtures: a tiny synthetic connectome (no network, no real data)."""

from __future__ import annotations

import sys
import time
from pathlib import Path

import numpy as np
import pytest

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app.audio import Features  # noqa: E402
from app.config import Settings  # noqa: E402
from app.connectome import Connectome, Population, save_connectome  # noqa: E402


def make_model(seed: int = 5, n_extra: int = 220) -> Connectome:
    """JO input populations, three output populations and random wiring."""
    rng = np.random.default_rng(seed)
    sizes = {"jo_a": 16, "jo_b": 16, "jo_ce": 16, "approach": 12, "startle": 6, "arousal": 8}
    ids = {"jo_a": 1, "jo_b": 2, "jo_ce": 3, "approach": 4, "startle": 5, "arousal": 6}
    population = []
    for key, size in sizes.items():
        population += [ids[key]] * size
    population += [0] * n_extra
    population = np.array(population, dtype=np.uint8)
    n = population.shape[0]
    inputs = np.flatnonzero(population <= 3)
    outputs = np.flatnonzero((population >= 4) & (population <= 6))
    rows: list[list[tuple[int, float]]] = [[] for _ in range(n)]
    for pre in range(n):
        for post in rng.choice(n, size=10, replace=False):
            if post != pre:
                rows[pre].append((int(post), float(rng.integers(1, 25) * (1 if rng.random() > 0.25 else -1))))
    for post in outputs:  # direct, strong drive so every output population can respond
        for pre in rng.choice(inputs, size=6, replace=False):
            rows[int(pre)].append((int(post), 60.0))
    indptr = np.zeros(n + 1, dtype=np.int64)
    indices: list[int] = []
    weights: list[float] = []
    for i, row in enumerate(rows):
        indptr[i + 1] = indptr[i] + len(row)
        indices += [r[0] for r in row]
        weights += [r[1] for r in row]
    labels = {
        "jo_a": "JO-A (high-frequency sound)",
        "jo_b": "JO-B (low-frequency sound, beat)",
        "jo_ce": "JO-C/E (slow deflection, envelope)",
        "approach": "Approach / courtship song",
        "startle": "Startle / escape",
        "arousal": "Auditory arousal (AMMC / wedge)",
    }
    populations = [
        Population(ids[k], k, labels[k], "input" if ids[k] <= 3 else "output") for k in sizes
    ]
    return Connectome(
        indptr=indptr,
        indices=np.array(indices, dtype=np.int32),
        weights=np.array(weights, dtype=np.float32),
        root_ids=np.arange(n, dtype=np.int64) + 720575940000000000,
        population=population,
        cell_type_index=rng.integers(0, 3, size=n).astype(np.uint16),
        cell_types=["JO-A1", "pC1a", "WED001"],
        positions=rng.normal(size=(n, 3)).astype(np.float32) * 100,
        silhouette=rng.normal(size=(500, 3)).astype(np.float32) * 120,
        populations=populations,
        meta={"dataVersion": "flywire-783", "scope": "auditory", "totalSynapseCount": 12345},
    )


@pytest.fixture(scope="session")
def fixture_model_path(tmp_path_factory) -> Path:
    path = tmp_path_factory.mktemp("model") / "fixture.npz"
    save_connectome(path, make_model())
    return path


def make_settings(model_path: Path, tmp_path: Path, **overrides) -> Settings:
    base = dict(
        enabled=True,
        model_path=model_path,
        data_dir=tmp_path,
        sim_speed=20.0,
        shuffle_trials=2,
        shared_secret="",
    )
    base.update(overrides)
    return Settings(**base)


def fake_features(loudness: float = 0.8, low: float = 0.3, mid: float = 0.4, high: float = 0.3, onset: float = 3.0):
    return Features(low, mid, high, loudness, onset, 1500.0, (1.0, 0.8, 1.2, 1.0))


def wait_ready(client, timeout: float = 60.0) -> dict:
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = client.get("/health", headers=getattr(client, "_flydj_headers", None))
        if last.status_code == 200 and last.json().get("modelStatus") == "ready":
            return last.json()
        if last.status_code == 503:
            raise AssertionError(f"model failed: {last.text}")
        time.sleep(0.05)
    raise AssertionError(f"model did not become ready: {last and last.text}")
