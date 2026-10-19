# FlyDJ sidecar

Optional, experimental service for SonosControl: it listens to a few seconds of
live radio streams, turns the sound into Poisson input for the Johnston's organ
neurons of a **real fruit-fly connectome** (FlyWire 783) and simulates the
network with the leaky integrate-and-fire model of Shiu et al. 2024. The
activity of three output populations (approach, startle, arousal) scores each
station; spikes can be streamed live for a 3D view. See `API.md` for the HTTP
and WebSocket contract used by `SonosControl.Web`, and `NOTICE.md` for data
licences and attribution.

```text
app/            service (config, connectome, simulator, audio, readout, stream, engine, main)
tools/          build_connectome.py (data -> npz), readouts.yaml (populations), bench.py
tests/          pytest suite (synthetic data, no network)
Dockerfile      multi-stage build, bakes the auditory connectome artifact
```

## Run locally

Python 3.13 is what the image uses; 3.12 to 3.14 work (numba and PyAV ship wheels).

```bash
python3 -m venv ../artifacts/flydj-venv
../artifacts/flydj-venv/bin/pip install -r requirements-dev.txt

# build the auditory artifact (downloads ~135 MB once into ../artifacts/flydj-data, SHA-256 verified)
../artifacts/flydj-venv/bin/python tools/build_connectome.py \
    --data-dir ../artifacts/flydj-data --out model/connectome_auditory.npz

# serve on :8090 (optionally FLYDJ_SHARED_SECRET=... to require X-FlyDj-Token)
FLYDJ_ENABLED=true ../artifacts/flydj-venv/bin/python -m app

../artifacts/flydj-venv/bin/python -m pytest tests -q
../artifacts/flydj-venv/bin/python tools/bench.py model/connectome_auditory.npz
```

`python -m app` exits immediately with status 0 unless `FLYDJ_ENABLED` is `true`.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `FLYDJ_ENABLED` | unset | must be `true` (any case) to start the server |
| `FLYDJ_PORT` | `8090` | listen port |
| `FLYDJ_CONNECTOME_SCOPE` | `auditory` | selects the baked artifact: `auditory` (8,000-neuron subgraph) or `full` (whole brain, runs slower than real time) |
| `FLYDJ_SIM_SPEED` | `1.0` | continuous mode runs at this multiple of real time |
| `FLYDJ_SHARED_SECRET` | empty | when set, `X-FlyDj-Token` is required on every request and the WS upgrade |
| `FLYDJ_DATA_DIR` | `/data` | reserved; currently unused |
| `FLYDJ_MODEL_PATH` | `model/connectome_<scope>.npz` | load this artifact instead (takes precedence over the scope) |
| `FLYDJ_SEED` | `783` | seed of all simulations (results are reproducible) |
| `FLYDJ_AMBIENT_HZ` | `2` | faint spontaneous Johnston's-organ rate that defines the resting baseline |

## Connectome artifact

`tools/build_connectome.py` reads the pinned FlyWire/Shiu files and writes one
compressed `.npz` (CSR graph with signed synapse counts, root ids, population
and cell-type labels, positions in micrometres with +y dorsal and +z anterior,
and a 30 000 point silhouette of the whole brain).

- `--scope auditory` (default): seeds (JO-A/B/C/E) and readouts, neurons on the
  strongest hop-shortest paths from seeds to each readout cell type, then the
  best-connected neurons within two hops (edges of at least 5 synapses), capped
  at `--max-neurons` (default 8000).
- `--scope full`: every neuron (about 54 MB). The Dockerfile builds both
  artifacts and ships them as `/srv/model/connectome_auditory.npz` and
  `/srv/model/connectome_full.npz`; nothing is downloaded or built at runtime.
  If the selected file is missing the service reports `error` ("... artifact not
  found; rebuild the image"). The builder stays importable for local use
  (`tools.build_connectome.build(...)`, with a progress callback).

The population definitions and the literature behind them are in
`tools/readouts.yaml`; the build fails with a clear message if any population
matches no neuron.

## Notes

- Loads: `stopped` costs nothing, `bursting` simulates only inside
  `/v1/audition` and `/v1/listen`, `continuous` adds a paced live simulation
  while a WebSocket viewer is connected. Audition and listen run on a separate
  simulator state, so they never disturb the live picture.
- Safety of fetching: only http/https, no loopback, link-local (169.254/16,
  fe80::/10) or unspecified addresses (checked on every redirect hop), 2 MB and
  8 s limits, at most 4 s of audio decoded, HLS is rejected.
- Tests need no network. `tests/test_brian_parity.py` runs only if `brian2` is
  installed.
