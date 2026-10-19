"""Runtime configuration read from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

PACKAGE_ROOT = Path(__file__).resolve().parent.parent
MODEL_DIR = PACKAGE_ROOT / "model"
DEFAULT_MODEL_PATH = MODEL_DIR / "connectome_auditory.npz"


def _truthy(value: str | None) -> bool:
    return (value or "").strip().lower() == "true"


def _float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None or not raw.strip():
        return default
    try:
        return float(raw)
    except ValueError:
        return default


def _int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or not raw.strip():
        return default
    try:
        return int(raw)
    except ValueError:
        return default


@dataclass(frozen=True)
class Settings:
    enabled: bool = False
    port: int = 8090
    scope: str = "auditory"
    sim_speed: float = 1.0
    shared_secret: str = ""
    data_dir: Path = Path("/data")
    model_path: Path = DEFAULT_MODEL_PATH
    seed: int = 783
    ambient_hz: float = 2.0
    shuffle_trials: int = 8

    @classmethod
    def from_env(cls) -> "Settings":
        scope = os.environ.get("FLYDJ_CONNECTOME_SCOPE", "auditory").strip().lower()
        if scope not in ("auditory", "full"):
            scope = "auditory"
        model_path = os.environ.get("FLYDJ_MODEL_PATH", "").strip()
        speed = _float("FLYDJ_SIM_SPEED", 1.0)
        return cls(
            enabled=_truthy(os.environ.get("FLYDJ_ENABLED")),
            port=_int("FLYDJ_PORT", 8090),
            scope=scope,
            sim_speed=min(max(speed, 0.05), 20.0),
            shared_secret=os.environ.get("FLYDJ_SHARED_SECRET", ""),
            data_dir=Path(os.environ.get("FLYDJ_DATA_DIR", "/data")),
            model_path=Path(model_path) if model_path else MODEL_DIR / f"connectome_{scope}.npz",
            seed=_int("FLYDJ_SEED", 783),
            ambient_hz=max(0.0, _float("FLYDJ_AMBIENT_HZ", 2.0)),
        )
