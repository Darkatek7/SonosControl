"""Service logic: model lifecycle, load modes, audition, listen and streaming.

Load modes
----------
``stopped``     no simulation at all; the loops wait on events.
``bursting``    running without viewers; the simulator only advances inside
                ``audition`` and ``listen`` requests (worker threads).
``continuous``  running with at least one stream viewer; chunks of 33 ms of
                biological time are simulated, paced to ``FLYDJ_SIM_SPEED`` x
                real time, and frames are broadcast at about 15 Hz.

Two independent simulator states share the same synapse arrays: ``live`` feeds
the continuous stream, ``burst`` serves audition, listen and the baseline, so
a request never disturbs the picture on screen. Each state is guarded by its
own lock.
"""

from __future__ import annotations

import asyncio
import logging
import math
import threading
from concurrent.futures import ThreadPoolExecutor
from typing import Callable

import numpy as np

from . import audio as audio_mod
from .audio import AudioError, Features, FeatureCache
from .config import Settings
from .connectome import Connectome, load_connectome
from .readout import WARMUP_MS, Baseline, compute_baseline, population_rates
from .simulator import Graph, Simulator, shuffled_graph, warm_up
from .stream import ViewerHub, encode_frame, encode_geometry

log = logging.getLogger("flydj")

DATA_VERSION = "flywire-783"
CHUNK_MS = 33.0  # two chunks per frame at ~15 Hz
FRAME_MIN_BIO_MS = 60.0
FRAME_MIN_WALL_S = 0.05
PING_INTERVAL_S = 1.0
MAX_CANDIDATES = 8
MIN_BIO_MS, MAX_BIO_MS, DEFAULT_BIO_MS = 100.0, 2000.0, 500.0
LISTEN_BIO_MS = 1000.0
ENGAGEMENT_TAU_S = 1.0

ATTRIBUTION = [
    {
        "text": "Dorkenwald et al. 2024, Neuronal wiring diagram of an adult brain, Nature (FlyWire connectome, CC BY 4.0)",
        "url": "https://doi.org/10.1038/s41586-024-07558-y",
    },
    {
        "text": "Schlegel et al. 2024, Whole-brain annotation and multi-connectome cell typing of Drosophila, Nature",
        "url": "https://doi.org/10.1038/s41586-024-07686-5",
    },
    {
        "text": "Shiu et al. 2024, A Drosophila computational brain model reveals sensorimotor processing, Nature (model, MIT)",
        "url": "https://doi.org/10.1038/s41586-024-07763-9",
    },
    {
        "text": "Buhmann et al. 2021, Automatic detection of synaptic partners in a whole-brain Drosophila EM dataset, Nature Methods",
        "url": "https://doi.org/10.1038/s41592-021-01183-7",
    },
    {
        "text": "Eckstein et al. 2024, Neurotransmitter classification from electron microscopy images at synaptic sites, Cell",
        "url": "https://doi.org/10.1016/j.cell.2024.03.016",
    },
]


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message


def _round(value: float | None, digits: int = 3) -> float | None:
    if value is None or not math.isfinite(value):
        return None
    return round(float(value), digits)


class Engine:
    def __init__(
        self,
        settings: Settings,
        audio_sampler: Callable[[str], Features] | None = None,
    ) -> None:
        self.settings = settings
        self.cache = FeatureCache()
        self.audio_sampler: Callable[[str], Features] = audio_sampler or (
            lambda url: audio_mod.sample_stream(url, self.cache)
        )
        self.pool = ThreadPoolExecutor(max_workers=MAX_CANDIDATES, thread_name_prefix="flydj-audio")

        self.status = "loading"
        self.message: str | None = None
        self.build_progress = 0.0
        self.model: Connectome | None = None
        self.live: Simulator | None = None
        self.burst: Simulator | None = None
        self.baseline: Baseline | None = None
        self.shuffle_control: dict | None = None
        self._geometry: bytes | None = None
        self._geometry_etag: str | None = None

        self.running = False
        self.listening_url: str | None = None
        self.listen_features: Features | None = None
        self.listen_rates: dict[str, float] | None = None
        self.engagement: float | None = None
        self.output_rates: dict[str, float] = {}
        self.realtime_factor = 0.0
        self._perf_sim: Simulator | None = None
        self._stim_dirty = True

        self.hub = ViewerHub(self._viewers_changed)
        self._loop: asyncio.AbstractEventLoop | None = None
        self._continuous: asyncio.Event | None = None
        self._ping_gate: asyncio.Event | None = None
        self._step_lock: asyncio.Lock | None = None
        self._tasks: list[asyncio.Task] = []
        self._pop_ids: np.ndarray = np.zeros(0, dtype=np.uint8)
        self._pop_sizes: np.ndarray = np.ones(0)
        self._bio_offset = 0.0

    # ------------------------------------------------------------------ lifecycle

    async def start(self) -> None:
        self._loop = asyncio.get_running_loop()
        self._continuous = asyncio.Event()
        self._ping_gate = asyncio.Event()
        self._step_lock = asyncio.Lock()
        self._tasks = [
            asyncio.create_task(self._load_task(), name="flydj-load"),
            asyncio.create_task(self._sim_loop(), name="flydj-sim"),
            asyncio.create_task(self._ping_loop(), name="flydj-ping"),
        ]

    async def shutdown(self) -> None:
        for task in self._tasks:
            task.cancel()
        for task in self._tasks:
            try:
                await task
            except (asyncio.CancelledError, Exception):  # noqa: BLE001
                pass
        self.pool.shutdown(wait=False, cancel_futures=True)

    async def _load_task(self) -> None:
        try:
            await asyncio.to_thread(self._load_blocking)
        except Exception as exc:  # noqa: BLE001
            log.exception("model load failed")
            self.status = "error"
            self.message = str(exc) or exc.__class__.__name__
        self._update_flags()
        if self.status == "ready":
            threading.Thread(target=self._shuffle_control_blocking, name="flydj-shuffle", daemon=True).start()

    # ------------------------------------------------------------------ model loading

    def _load_blocking(self) -> None:
        path = self.settings.model_path
        self.status = "loading"
        self.message = "loading the connectome"
        if not path.exists():
            raise FileNotFoundError(
                f"{self.settings.scope} connectome artifact not found at {path}; rebuild the image "
                "(or set FLYDJ_MODEL_PATH to an existing artifact)"
            )
        model = load_connectome(path)
        model.validate_required()
        self.message = "compiling the simulator"
        warm_up()
        graph = Graph(model.indptr, model.indices, model.weights)
        groups = {key: model.population_indices(key) for key in model.input_keys()}
        live = Simulator(graph, groups, seed=self.settings.seed)
        burst = live.sibling(self.settings.seed + 1)
        self.message = "measuring the resting baseline"
        baseline = compute_baseline(burst, model, self.settings.ambient_hz, self.settings.seed)
        self.model, self.live, self.burst, self.baseline = model, live, burst, baseline
        self._pop_ids = np.array([p.id for p in model.populations], dtype=np.uint8)
        self._pop_sizes = np.array([max(model.population_size(p.key), 1) for p in model.populations], dtype=np.float64)
        self.output_rates = {key: 0.0 for key in model.output_keys()}
        self._geometry = None
        self._bio_offset = live.bio_ms + burst.bio_ms  # load-time work is not "simulated time"
        self._perf_sim = burst
        self.build_progress = 1.0
        self.message = None
        self.status = "ready"
        log.info(
            "model ready: %d neurons, %d connections (%s scope)",
            model.n_neurons,
            model.n_connections,
            model.meta.get("scope"),
        )

    def _shuffle_control_blocking(self) -> None:
        try:
            model, live = self.model, self.live
            if model is None or live is None:
                return
            trials = self.settings.shuffle_trials if model.n_neurons <= 20000 else 3
            real = live.sibling(self.settings.seed + 11)
            null = Simulator(
                shuffled_graph(live.graph, self.settings.seed + 5),
                live.stim_groups,
                live.params,
                self.settings.seed + 12,
            )
            reference = {key: 100.0 for key in model.input_keys()}
            keys = model.output_keys()
            sizes = np.array([model.population_size(k) for k in keys], dtype=np.float64)

            def response(sim: Simulator, trial: int) -> float:
                counts = sim.run_trial(reference, 500.0, warmup_ms=WARMUP_MS, seed=self.settings.seed + 100 + trial)
                rates = population_rates(counts, model, keys, 500.0)
                return float(sum(rates[k] * s for k, s in zip(keys, sizes)) / sizes.sum())

            real_vals = [response(real, t) for t in range(trials)]
            null_vals = [response(null, t) for t in range(trials)]
            real_mean, null_mean = float(np.mean(real_vals)), float(np.mean(null_vals))
            self.shuffle_control = {
                "realResponse": _round(real_mean),
                "shuffledResponse": _round(null_mean),
                "ratio": _round(real_mean / null_mean) if null_mean > 0 else None,
                "trials": trials,
            }
            log.info("shuffle control: %s", self.shuffle_control)
        except Exception:  # noqa: BLE001
            log.exception("shuffle control failed")

    # ------------------------------------------------------------------ modes

    @property
    def ready(self) -> bool:
        return self.status == "ready"

    @property
    def mode(self) -> str:
        if not self.running:
            return "stopped"
        return "continuous" if self.hub.count > 0 and self.ready else "bursting"

    @property
    def total_steps(self) -> int:
        return (self.live.steps_total if self.live else 0) + (self.burst.steps_total if self.burst else 0)

    @property
    def bio_time_ms(self) -> float:
        total = (self.live.bio_ms if self.live else 0.0) + (self.burst.bio_ms if self.burst else 0.0)
        return max(0.0, total - self._bio_offset)

    def _viewers_changed(self, _count: int) -> None:
        self._update_flags()

    def _update_flags(self) -> None:
        if self._continuous is None or self._ping_gate is None:
            return
        continuous = self.running and self.ready and self.hub.count > 0
        if continuous:
            self._continuous.set()
        else:
            self._continuous.clear()
        if self.hub.count > 0 and not continuous:
            self._ping_gate.set()
        else:
            self._ping_gate.clear()

    async def run_start(self) -> dict:
        self.running = True
        self._stim_dirty = True
        self._update_flags()
        return self.state()

    async def run_stop(self) -> dict:
        self.running = False
        self.realtime_factor = 0.0
        self._update_flags()
        # wait until no chunk or burst is in flight so that "stopped" means no steps
        assert self._step_lock is not None
        async with self._step_lock:
            pass
        await asyncio.to_thread(self._wait_idle)
        return self.state()

    def _wait_idle(self) -> None:
        for sim in (self.live, self.burst):
            if sim is not None:
                with sim.lock:
                    pass

    # ------------------------------------------------------------------ continuous loop

    def _advance_live(self) -> tuple[np.ndarray, np.ndarray, float]:
        """Simulate one chunk on the live state; return spikes and population counts."""
        live = self.live
        assert live is not None and self.model is not None
        with live.lock:
            if self._stim_dirty:
                self._apply_stimulus(live, gain=1.0)
                self._stim_dirty = False
            elif self.listen_features is not None and self.listen_features.envelope:
                self._apply_stimulus(live, gain=self._envelope_gain(live.bio_ms))
            n_steps = int(round(CHUNK_MS / live.dt))
            batch = live.run(n_steps)
        pop_of_spike = self.model.population[batch.neurons]
        counts = np.bincount(pop_of_spike, minlength=256)[self._pop_ids].astype(np.float64)
        return batch.neurons, counts, n_steps * live.dt

    def _envelope_gain(self, bio_ms: float) -> float:
        env = self.listen_features.envelope if self.listen_features else ()
        if not env:
            return 1.0
        slot = int(bio_ms // 250.0) % len(env)
        return float(min(max(env[slot], 0.3), 1.7))

    def _apply_stimulus(self, sim: Simulator, gain: float) -> None:
        if self.listen_rates:
            sim.set_rates({k: min(v * gain, audio_mod.MAX_RATE_HZ) for k, v in self.listen_rates.items()})
        else:
            sim.set_rates({key: self.settings.ambient_hz for key in (self.model.input_keys() if self.model else [])})

    async def _sim_loop(self) -> None:
        assert self._continuous is not None and self._step_lock is not None
        loop = asyncio.get_running_loop()
        spikes: list[np.ndarray] = []
        pop_counts = np.zeros(0)
        acc_ms = 0.0
        last_frame = loop.time()
        smoothed: dict[str, float] = {}
        while True:
            await self._continuous.wait()
            t0 = loop.time()
            async with self._step_lock:
                if not self._continuous.is_set():
                    continue
                try:
                    idx, counts, chunk_ms = await asyncio.to_thread(self._advance_live)
                except Exception:  # noqa: BLE001
                    log.exception("simulation step failed")
                    await asyncio.sleep(1.0)
                    continue
            self._perf_sim = self.live
            spikes.append(idx)
            pop_counts = counts if pop_counts.shape != counts.shape else pop_counts + counts
            acc_ms += chunk_ms
            now = loop.time()
            if acc_ms >= FRAME_MIN_BIO_MS and now - last_frame >= FRAME_MIN_WALL_S:
                rates = pop_counts / (self._pop_sizes * (acc_ms / 1000.0))
                alpha = 1.0 - math.exp(-(now - last_frame) / ENGAGEMENT_TAU_S)
                assert self.model is not None
                for p, rate in zip(self.model.populations, rates):
                    if p.role == "output":
                        smoothed[p.key] = smoothed.get(p.key, 0.0) + alpha * (float(rate) - smoothed.get(p.key, 0.0))
                self.output_rates = dict(smoothed)
                if self.listen_rates and self.baseline is not None:
                    self.engagement = self.baseline.score(smoothed)
                merged = np.concatenate(spikes) if len(spikes) > 1 else spikes[0]
                self.hub.broadcast(encode_frame(self.bio_time_ms, self.engagement, rates, merged))
                spikes, pop_counts, acc_ms, last_frame = [], np.zeros(0), 0.0, now
            elapsed = loop.time() - t0
            target = chunk_ms / 1000.0 / self.settings.sim_speed
            if elapsed < target:
                await asyncio.sleep(target - elapsed)
            wall = loop.time() - t0
            instantaneous = (chunk_ms / 1000.0) / max(wall, 1e-6)
            self.realtime_factor += 0.2 * (instantaneous - self.realtime_factor)

    async def _ping_loop(self) -> None:
        assert self._ping_gate is not None
        while True:
            await self._ping_gate.wait()
            self.hub.broadcast(self.empty_frame())
            await asyncio.sleep(PING_INTERVAL_S)

    def empty_frame(self) -> bytes:
        count = len(self.model.populations) if self.model else 0
        return encode_frame(self.bio_time_ms, self.engagement, np.zeros(count, dtype=np.float32), None)

    # ------------------------------------------------------------------ audio

    async def _features_for(self, url: str) -> tuple[Features | None, str | None, str | None]:
        loop = asyncio.get_running_loop()
        try:
            features = await loop.run_in_executor(self.pool, self.audio_sampler, url)
            return features, None, None
        except AudioError as exc:
            return None, exc.code, exc.message
        except Exception:  # noqa: BLE001
            log.exception("unexpected audio failure for %s", url)
            return None, "fetch_failed", "The stream could not be analysed."

    def _require_ready(self) -> None:
        if self.status == "error":
            raise ApiError(503, "model_error", self.message or "The connectome model failed to load.")
        if not self.ready:
            raise ApiError(503, "model_not_ready", f"The connectome model is {self.status}.")

    def _require_running(self) -> None:
        if not self.running:
            raise ApiError(409, "not_running", "The FlyDJ simulation is stopped.")

    def _measure(self, rates: dict[str, float], bio_ms: float, seed_offset: int = 7) -> dict[str, float]:
        """Run a burst on the burst state; return output-population rates (Hz)."""
        assert self.burst is not None and self.model is not None
        counts = self.burst.run_trial(rates, bio_ms, warmup_ms=WARMUP_MS, seed=self.settings.seed + seed_offset)
        self._perf_sim = self.burst
        return population_rates(counts, self.model, self.model.output_keys(), bio_ms)

    def _audition_blocking(self, items: list[tuple[str, Features | None, str | None]], bio_ms: float) -> list[dict]:
        assert self.baseline is not None
        results: list[dict] = []
        with self.burst.lock:  # type: ignore[union-attr]
            for cid, features, error in items:
                if features is None:
                    results.append(
                        {
                            "id": cid,
                            "ok": False,
                            "error": error or "fetch_failed",
                            "score": None,
                            "features": None,
                            "populationRates": None,
                        }
                    )
                    continue
                if not self.running:
                    raise ApiError(409, "not_running", "The FlyDJ simulation was stopped.")
                rates = self._measure(audio_mod.rates_from_features(features), bio_ms)
                results.append(
                    {
                        "id": cid,
                        "ok": True,
                        "error": None,
                        "score": _round(self.baseline.score(rates)),
                        "features": features.to_api(),
                        "populationRates": {k: _round(v) for k, v in rates.items()},
                    }
                )
        return results

    async def audition(self, candidates: list[tuple[str, str]], bio_ms: float) -> dict:
        self._require_ready()
        self._require_running()
        bio_ms = float(min(max(bio_ms, MIN_BIO_MS), MAX_BIO_MS))
        fetched = await asyncio.gather(*(self._features_for(url) for _, url in candidates))
        self._require_running()
        items = [(cid, f, err) for (cid, _), (f, err, _msg) in zip(candidates, fetched)]
        results = await asyncio.to_thread(self._audition_blocking, items, bio_ms)
        assert self.baseline is not None
        return {
            "results": results,
            "bioMs": int(round(bio_ms)),  # API.md: integer milliseconds
            "baseline": {k: _round(v) for k, v in self.baseline.mean.items()},
        }

    async def listen(self, url: str | None) -> dict:
        if url is None or not str(url).strip():
            self.listening_url = None
            self.listen_rates = None
            self.listen_features = None
            self.engagement = None
            self._stim_dirty = True
            return {"ok": True, "error": None, "engagement": None, "features": None, "populationRates": None}
        self._require_ready()
        self._require_running()
        features, error, _message = await self._features_for(url)
        if features is None:
            self.listening_url = None
            self.listen_rates = None
            self.listen_features = None
            self.engagement = None
            self._stim_dirty = True
            return {
                "ok": False,
                "error": error or "fetch_failed",
                "engagement": None,
                "features": None,
                "populationRates": None,
            }
        rates = audio_mod.rates_from_features(features)

        def burst() -> dict[str, float]:
            with self.burst.lock:  # type: ignore[union-attr]
                self._require_running()
                return self._measure(rates, LISTEN_BIO_MS)

        output = await asyncio.to_thread(burst)
        assert self.baseline is not None
        self.listening_url = str(url).strip()
        self.listen_features = features
        self.listen_rates = rates
        self.engagement = self.baseline.score(output)
        self.output_rates = dict(output)
        self._stim_dirty = True
        return {
            "ok": True,
            "error": None,
            "engagement": _round(self.engagement),
            "features": features.to_api(),
            "populationRates": {k: _round(v) for k, v in output.items()},
        }

    # ------------------------------------------------------------------ documents

    def state(self) -> dict:
        perf = self._perf_sim.perf if self._perf_sim else None
        return {
            "mode": self.mode,
            "running": self.running,
            "viewers": self.hub.count,
            "bioTimeMs": _round(self.bio_time_ms, 1),
            "realtimeFactor": _round(self.realtime_factor if self.mode == "continuous" else 0.0, 3),
            "cpuMsPerBioSecond": _round(perf.cpu_ms_per_bio_second if perf else 0.0, 1),
            "spikesPerSecond": _round(perf.spikes_per_bio_second if perf else 0.0, 1),
            "engagement": _round(self.engagement),
            "populationRates": {k: _round(v) for k, v in self.output_rates.items()},
            "listeningUrl": self.listening_url,
            "lastFeatures": self.listen_features.to_api() if self.listen_features else None,
            "modelStatus": self.status,
        }

    def model_doc(self) -> dict:
        model = self.model
        meta = model.meta if model else {}
        return {
            "dataVersion": meta.get("dataVersion", DATA_VERSION),
            "scope": meta.get("scope", self.settings.scope),
            "status": self.status,
            "buildProgress": _round(self.build_progress),
            "message": self.message,
            "neuronCount": model.n_neurons if model else 0,
            "connectionCount": model.n_connections if model else 0,
            "synapseCount": model.synapse_count if model else 0,
            "populations": model.describe_populations() if model else [],
            "attribution": ATTRIBUTION,
            "shuffleControl": self.shuffle_control,
        }

    def geometry(self) -> tuple[bytes, str]:
        self._require_ready()
        if self._geometry is None:
            import hashlib

            assert self.model is not None
            data = encode_geometry(self.model)
            self._geometry = data
            self._geometry_etag = '"' + hashlib.sha256(data).hexdigest()[:32] + '"'
        assert self._geometry_etag is not None
        return self._geometry, self._geometry_etag
