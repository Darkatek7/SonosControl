# FlyDJ sidecar API contract

Internal HTTP API of the `flydj` Python service. It is reachable only on the Docker
Compose network (default `http://flydj:8090`) and is consumed exclusively by
`SonosControl.Web`. Browsers never talk to it directly; the web app relays the
geometry and the live stream behind its own cookie authentication.

## Conventions

- JSON bodies use camelCase property names.
- When `FLYDJ_SHARED_SECRET` is set, every request (including the WebSocket
  upgrade) must send `X-FlyDj-Token: <secret>`; otherwise the service answers `401`.
- Errors use `{"error": "<code>", "message": "<text>"}` with a 4xx/5xx status.
- All binary payloads are little-endian.

## Load modes

| Mode | When | Simulation cost |
|---|---|---|
| `stopped` | after start-up and after `POST /v1/run/stop` | none; the loop waits on an event |
| `bursting` | running, no stream viewer | only short bursts for `audition` and `listen` |
| `continuous` | running and at least one `/v1/stream` viewer | continuous, throttled to `FLYDJ_SIM_SPEED` x real time |

`audition` and `listen` return `409 not_running` while the mode is `stopped`.

## Endpoints

### `GET /health`

`200 {"status": "ok", "modelStatus": "ready"}`. `modelStatus` is `loading`,
`ready` or `error` (`building` is reserved and currently never reported, because
both connectome artifacts are baked into the image). Health is `200` while
loading so the container is not restarted; it is `503` only when `error`.

### `GET /v1/model`

```json
{
  "dataVersion": "flywire-783",
  "scope": "auditory",
  "status": "ready",
  "buildProgress": 1.0,
  "message": null,
  "neuronCount": 7984,
  "connectionCount": 912345,
  "synapseCount": 4567890,
  "populations": [
    {"id": 1, "key": "jo_a", "label": "JO-A (high-frequency sound)", "role": "input", "neuronCount": 120},
    {"id": 5, "key": "approach", "label": "Approach / courtship song", "role": "output", "neuronCount": 40}
  ],
  "attribution": [
    {"text": "Dorkenwald et al. 2024, Neuronal wiring diagram of an adult brain, Nature", "url": "https://doi.org/10.1038/s41586-024-07558-y"}
  ],
  "shuffleControl": {"realResponse": 2.41, "shuffledResponse": 0.37, "ratio": 6.5, "trials": 8}
}
```

Population `role` is `input`, `output` or `other`. Population ids are `1..255`;
`0` means "not part of a named population". Required keys: input populations
`jo_a`, `jo_b`, `jo_ce`; output populations `approach`, `startle`, `arousal`.
`shuffleControl` is `null` until computed; its `ratio` is `null` when the
shuffled brain stays silent (as in the full scope).

### `GET /v1/model/geometry`

`application/octet-stream`, with a strong `ETag` (send `If-None-Match` for `304`).

| Offset | Type | Content |
|---|---|---|
| 0 | 4 x u8 | magic `FDJG` |
| 4 | u32 | header length `H` in bytes |
| 8 | H x u8 | UTF-8 JSON header, then zero padding to a 4-byte boundary |
| ... | f32[N*3] | neuron positions in micrometres, centred on the brain centroid (x, y, z) |
| ... | f32[S*3] | silhouette point positions, same frame |
| ... | u16[N] | cell-type index into `header.cellTypes` (`65535` = unknown) |
| ... | u8[N] | population id (`0` = none) |

Header JSON: `{"version": 1, "neuronCount": N, "silhouetteCount": S,
"populations": [{"id", "key", "label", "role"}], "cellTypes": ["JO-A", ...]}`.
Neuron index `i` in this file is the same index used in stream spike lists.

### `POST /v1/run/start` and `POST /v1/run/stop`

No body. Both are idempotent and return the `GET /v1/state` document.

### `POST /v1/audition`

Plays a short live sample of every candidate stream to the simulated brain and
reports how its output populations respond.

Request:

```json
{"candidates": [{"id": "st-1", "url": "stream.example.com/live.mp3"}], "bioMs": 500, "sampleSeconds": 10, "fresh": true}
```

`url` may lack a scheme (`http://` is assumed); `.m3u`/`.pls` playlists are
resolved. Only `http`/`https` is fetched, at most 2 MB per candidate with an
18-second network budget per URL resolution. `sampleSeconds` requests 0.8 to
10 seconds of decoded audio (default 4). `fresh: true` bypasses the 120-second
feature cache; the default is false. At most two streams are fetched concurrently.
`bioMs` is clamped to `100..2000` (default `500`). At most 8 candidates.
SonosControl requests fresh samples in batches of at most two and keeps a rolling
median for each allowed station. Update both images together: new SonosControl
requires sample identities and timestamps and reports a clear error for old sidecars.

Response:

```json
{
  "results": [
    {
      "id": "st-1",
      "ok": true,
      "error": null,
      "score": 1.27,
      "sampleId": "b4c9800d54cd4ac989fa21155f137857",
      "sampledAtUtc": "2026-10-20T06:23:20+00:00",
      "features": {"low": 0.41, "mid": 0.33, "high": 0.26, "loudness": 0.62, "onsetRate": 2.1, "centroidHz": 1840.0},
      "populationRates": {"approach": 14.2, "startle": 3.1, "arousal": 22.0}
    }
  ],
  "bioMs": 500,
  "baseline": {"approach": 2.0, "startle": 1.1, "arousal": 4.0}
}
```

`score = z(approach) - z(startle)` against the no-stimulus baseline. A failed
candidate has `ok: false`, an `error` code and `score: null`. Fresh requests also
assign an identity and timestamp to failures so the UI can report failed probes.
Successful cached responses retain the original measurement identity and time;
repeated requests do not create new independent evidence.

### `POST /v1/listen`

Sets the currently playing stream as the continuous stimulus (used by the live
view) and measures engagement with a 1 s burst.

Request `{"url": "stream.example.com/live.mp3"}` or `{"url": null}` to clear.
Response `{"ok": true, "error": null, "engagement": 0.84, "features": {...}, "populationRates": {...}}`.
`engagement` uses the same formula as the audition `score`.

### `GET /v1/state`

```json
{
  "mode": "bursting",
  "running": true,
  "viewers": 0,
  "bioTimeMs": 123456.7,
  "realtimeFactor": 0.92,
  "cpuMsPerBioSecond": 310.5,
  "spikesPerSecond": 1840.0,
  "engagement": 0.84,
  "populationRates": {"approach": 14.2, "startle": 3.1, "arousal": 22.0},
  "listeningUrl": "stream.example.com/live.mp3",
  "lastFeatures": {"low": 0.41, "mid": 0.33, "high": 0.26, "loudness": 0.62, "onsetRate": 2.1, "centroidHz": 1840.0},
  "modelStatus": "ready"
}
```

### `WS /v1/stream`

Server-to-client binary frames at about 15 Hz while the mode is `continuous`.
Connecting a client switches a running simulation to `continuous`; when the
last client disconnects it falls back to `bursting`. While stopped, the server
sends one frame per second with `spikeCount = 0` so clients can show "paused".

| Offset | Type | Content |
|---|---|---|
| 0 | u8 | magic `0xF1` |
| 1 | u8 | version `1` |
| 2 | u16 | population count `P` (same order as `/v1/model` populations) |
| 4 | u32 | spike count `K` |
| 8 | f64 | simulated biological time in ms |
| 16 | f32 | engagement (`NaN` when unknown) |
| 20 | f32[P] | population rates in Hz |
| 20+4P | u32[K] | indices of neurons that spiked since the previous frame |

Clients ignore frames whose magic or version they do not understand.
