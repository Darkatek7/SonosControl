"""Population responses, baseline statistics and the engagement score.

``score = z(approach) - z(startle)`` where ``z`` standardises a population's
mean firing rate (Hz per neuron) against the baseline distribution: the same
network driven only by faint spontaneous Johnston's organ activity, repeated
over several seeds. The standard deviation is the trial-to-trial spread with a
floor of ``STD_FLOOR_HZ`` because the baseline of a silent network has almost
no variance.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

from .connectome import Connectome
from .simulator import Simulator

STD_FLOOR_HZ = 1.0
BASELINE_TRIALS = 6
BASELINE_MS = 500.0
WARMUP_MS = 50.0


def population_rates(
    counts: np.ndarray, model: Connectome, keys: list[str], window_ms: float
) -> dict[str, float]:
    """Mean firing rate (Hz per neuron) of each population in ``keys``."""
    seconds = window_ms / 1000.0
    out: dict[str, float] = {}
    for key in keys:
        idx = model.population_indices(key)
        out[key] = float(counts[idx].sum() / (idx.shape[0] * seconds)) if idx.shape[0] and seconds > 0 else 0.0
    return out


@dataclass
class Baseline:
    mean: dict[str, float]
    std: dict[str, float]
    trials: int

    def z(self, key: str, rate: float) -> float:
        std = max(self.std.get(key, 0.0), STD_FLOOR_HZ)
        return (rate - self.mean.get(key, 0.0)) / std

    def score(self, rates: dict[str, float]) -> float:
        return self.z("approach", rates.get("approach", 0.0)) - self.z("startle", rates.get("startle", 0.0))


def compute_baseline(
    sim: Simulator,
    model: Connectome,
    ambient_hz: float,
    seed: int,
    trials: int = BASELINE_TRIALS,
    duration_ms: float = BASELINE_MS,
) -> Baseline:
    """Run the resting network ``trials`` times and summarise output rates."""
    keys = model.output_keys()
    ambient = {key: ambient_hz for key in model.input_keys()}
    samples: dict[str, list[float]] = {k: [] for k in keys}
    for trial in range(trials):
        counts = sim.run_trial(ambient, duration_ms, warmup_ms=WARMUP_MS, seed=seed + 1000 + trial)
        rates = population_rates(counts, model, keys, duration_ms)
        for key in keys:
            samples[key].append(rates[key])
    mean = {k: float(np.mean(v)) for k, v in samples.items()}
    std = {k: float(np.std(v)) for k, v in samples.items()}
    return Baseline(mean, std, trials)


def finite_or_none(value: float | None) -> float | None:
    if value is None or not math.isfinite(value):
        return None
    return float(value)
