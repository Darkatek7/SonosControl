import os
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent


@pytest.mark.parametrize("value", [None, "", "false", "0", "yes"])
def test_exits_zero_when_disabled(value):
    env = {k: v for k, v in os.environ.items() if not k.startswith("FLYDJ_")}
    if value is not None:
        env["FLYDJ_ENABLED"] = value
    result = subprocess.run(
        [sys.executable, "-m", "app"], cwd=ROOT, env=env, capture_output=True, text=True, timeout=30
    )
    assert result.returncode == 0
    lines = [line for line in result.stdout.splitlines() if line.strip()]
    assert len(lines) == 1 and "disabled" in lines[0].lower()
    assert "Traceback" not in result.stderr


def test_enabled_flag_is_case_insensitive():
    from app.config import Settings

    for value in ("true", "TRUE", "True"):
        os.environ["FLYDJ_ENABLED"] = value
        try:
            assert Settings.from_env().enabled
        finally:
            del os.environ["FLYDJ_ENABLED"]
    assert not Settings.from_env().enabled


def test_scope_selects_the_artifact_and_model_path_overrides(monkeypatch, tmp_path):
    from app.config import MODEL_DIR, Settings

    monkeypatch.delenv("FLYDJ_MODEL_PATH", raising=False)
    monkeypatch.setenv("FLYDJ_CONNECTOME_SCOPE", "full")
    assert Settings.from_env().model_path == MODEL_DIR / "connectome_full.npz"
    monkeypatch.setenv("FLYDJ_CONNECTOME_SCOPE", "auditory")
    assert Settings.from_env().model_path == MODEL_DIR / "connectome_auditory.npz"
    monkeypatch.setenv("FLYDJ_MODEL_PATH", str(tmp_path / "x.npz"))
    assert Settings.from_env().model_path == tmp_path / "x.npz"
