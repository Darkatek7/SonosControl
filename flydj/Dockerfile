# syntax=docker/dockerfile:1
#
# FlyDJ sidecar. Build context: this directory (flydj/).
#   docker build -t sonoscontrol-flydj flydj
#   docker buildx build --platform linux/amd64,linux/arm64 -t sonoscontrol-flydj flydj
#
# Stage "builder" downloads the pinned FlyWire/Shiu data (SHA-256 verified),
# builds the auditory subgraph and the full connectome and discards the raw
# downloads; only the two .npz artifacts reach the runtime image.
# FLYDJ_CONNECTOME_SCOPE (auditory|full) selects which one is loaded.

ARG PYTHON_IMAGE=python:3.13-slim

# ---------------------------------------------------------------- builder
FROM ${PYTHON_IMAGE} AS builder
ENV PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONDONTWRITEBYTECODE=1
WORKDIR /build
COPY requirements-build.txt ./
RUN python -m venv /opt/build-venv \
 && /opt/build-venv/bin/pip install -r requirements-build.txt
COPY app/ ./app/
COPY tools/ ./tools/
ARG FLYDJ_MAX_NEURONS=8000
RUN mkdir -p /out /raw \
 && /opt/build-venv/bin/python -I tools/build_connectome.py \
      --scope auditory --max-neurons "${FLYDJ_MAX_NEURONS}" \
      --data-dir /raw --out /out/connectome_auditory.npz \
 && /opt/build-venv/bin/python -I tools/build_connectome.py \
      --scope full --offline \
      --data-dir /raw --out /out/connectome_full.npz \
 && rm -rf /raw

# ------------------------------------------------------------------- deps
FROM ${PYTHON_IMAGE} AS deps
ENV PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1
COPY requirements.txt ./
RUN python -m venv /opt/venv \
 && /opt/venv/bin/pip install -r requirements.txt \
 && find /opt/venv -depth -type d -name tests -prune -exec rm -rf {} + \
 && rm -rf /opt/venv/share

# ---------------------------------------------------------------- runtime
FROM ${PYTHON_IMAGE} AS runtime
ENV PATH=/opt/venv/bin:$PATH \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    NUMBA_CACHE_DIR=/opt/flydj-cache \
    FLYDJ_PORT=8090 \
    FLYDJ_DATA_DIR=/data
RUN groupadd --system --gid 10001 flydj \
 && useradd --system --uid 10001 --gid flydj --home-dir /srv --shell /usr/sbin/nologin flydj \
 && mkdir -p /data /opt/flydj-cache \
 && chown flydj:flydj /data /opt/flydj-cache
COPY --from=deps /opt/venv /opt/venv
WORKDIR /srv
COPY app/ ./app/
COPY NOTICE.md ./NOTICE.md
COPY --from=builder /out/connectome_auditory.npz /out/connectome_full.npz ./model/
USER flydj
# compile the simulation kernel once so that the first start is fast
RUN python -c "from app.simulator import warm_up; warm_up()"
VOLUME ["/data"]
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD ["python", "-c", "import os,sys,urllib.request as u; r=u.Request('http://127.0.0.1:'+os.environ.get('FLYDJ_PORT','8090')+'/health', headers={'X-FlyDj-Token': os.environ.get('FLYDJ_SHARED_SECRET','')}); sys.exit(0 if u.urlopen(r, timeout=4).status == 200 else 1)"]
ENTRYPOINT ["python", "-m", "app"]
