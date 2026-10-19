"""Entrypoint: ``python -m app``.

Does nothing (exit code 0) unless ``FLYDJ_ENABLED`` is ``true``, so the container
can ship in a Compose file without costing anything when the feature is off.
"""

from __future__ import annotations

import os
import sys


def main() -> int:
    if os.environ.get("FLYDJ_ENABLED", "").strip().lower() != "true":
        print("FlyDJ is disabled (set FLYDJ_ENABLED=true to enable it); exiting.", flush=True)
        return 0

    import logging

    import uvicorn

    from .config import Settings
    from .main import create_app

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    settings = Settings.from_env()
    logging.getLogger("flydj").info(
        "starting FlyDJ on port %d (scope=%s, speed=%.2f)", settings.port, settings.scope, settings.sim_speed
    )
    uvicorn.run(
        create_app(settings),
        host="0.0.0.0",  # noqa: S104 - reachable on the Compose network only
        port=settings.port,
        log_level="info",
        access_log=False,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
