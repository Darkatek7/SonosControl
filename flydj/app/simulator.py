"""Leaky integrate-and-fire simulation of a connectome.

The neuron and synapse model follows Shiu et al. 2024 (Nature 634:210-219,
https://github.com/philshiu/Drosophila_brain_model, MIT licence), which is
written for Brian2::

    dv/dt = (v0 - v + g) / t_mbr      (unless refractory)
    dg/dt = -g / tau                  (unless refractory)
    spike when v > v_th; reset v = v_rst, g = 0
    synapse: g_post += w_syn * signed_synapse_count after a 1.8 ms delay
    PoissonInput(N=1, rate=r, weight=w_syn * 250) on v of every stimulated
    neuron, whose refractory period is set to 0
    (``unless refractory`` also freezes ``g`` against synaptic input: Brian2 makes
    every variable of such an equation conditionally writable, so events that
    arrive while the postsynaptic neuron is refractory are discarded)

Brian2's ``linear`` method integrates the coupled linear system exactly. We
do the same with a closed-form update per time step; the double exponential
has the coefficients ``A * (exp(-dt/tau) - exp(-dt/t_mbr))`` with
``A = tau / (tau - t_mbr)``.

Within one step the order mirrors Brian2's default schedule: integrate,
Poisson kicks, threshold, deliver synaptic events that become due, reset the
neurons that spiked (which also clears any ``g`` they just received) and then
schedule their outgoing events ``delay`` steps ahead.

The kernel is event driven for synapses (only rows of neurons that spiked are
visited) and integrates all neurons densely each step; an "active list" was
tried and was slower here because every spike touches ~100 targets, which then
stay away from rest for ~200 ms. Poisson drive samples the gaps between events
(geometric skipping), which is exactly an independent Bernoulli draw per
neuron and step at a cost proportional to the number of events. A whole chunk
of steps runs per call. It uses its own xorshift64* generator whose state
lives in a numpy array, so results are deterministic for a seed regardless of
the calling thread.
"""

from __future__ import annotations

import math
import threading
import time
from dataclasses import dataclass

import numpy as np
from numba import njit

# ---------------------------------------------------------------------------
# Parameters (Shiu et al. 2024, model.py ``default_params``)
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LifParams:
    v0: float = -52.0  # mV, resting potential
    v_rst: float = -52.0  # mV, reset potential
    v_th: float = -45.0  # mV, spike threshold
    t_mbr: float = 20.0  # ms, membrane time constant
    tau: float = 5.0  # ms, synaptic time constant
    t_rfc: float = 2.2  # ms, refractory period
    t_dly: float = 1.8  # ms, synaptic delay
    w_syn: float = 0.275  # mV per synapse
    f_poi: float = 250.0  # Poisson synapse scaling factor
    dt: float = 0.1  # ms, time step

    @property
    def poisson_kick(self) -> float:
        return self.w_syn * self.f_poi

    @property
    def delay_steps(self) -> int:
        return max(1, int(round(self.t_dly / self.dt)))

    @property
    def refractory_steps(self) -> int:
        # Brian2 leaves refractoriness once t - lastspike >= t_rfc, i.e. the
        # first integrated step is the 22nd after the spike: 21 frozen steps.
        return max(0, int(round(self.t_rfc / self.dt)) - 1)

    def consts(self) -> np.ndarray:
        em = math.exp(-self.dt / self.t_mbr)
        es = math.exp(-self.dt / self.tau)
        coupling = self.tau / (self.tau - self.t_mbr)
        return np.array([self.v0, self.v_rst, self.v_th, em, es, coupling * (es - em)], dtype=np.float64)


DEFAULT_PARAMS = LifParams()


@dataclass(frozen=True)
class Graph:
    """CSR graph of signed synapse counts, indexed by presynaptic neuron."""

    indptr: np.ndarray
    indices: np.ndarray
    weights: np.ndarray

    @property
    def n(self) -> int:
        return int(self.indptr.shape[0] - 1)


def shuffled_graph(graph: Graph, seed: int) -> Graph:
    """Degree-preserving null model.

    Every connection keeps its presynaptic neuron and weight but receives a
    random postsynaptic neuron drawn without replacement from the original
    multiset of targets, so each neuron keeps its out-degree, in-degree and
    outgoing weights while the wiring pattern is destroyed.
    """
    rng = np.random.default_rng(seed)
    return Graph(graph.indptr, rng.permutation(graph.indices), graph.weights)


# ---------------------------------------------------------------------------
# Numba kernel
# ---------------------------------------------------------------------------


@njit(cache=True, inline="always")
def _u01(state):
    x = state[0]
    x ^= x >> np.uint64(12)
    x ^= x << np.uint64(25)
    x ^= x >> np.uint64(27)
    state[0] = x
    r = x * np.uint64(2685821657736338717)
    return float(r >> np.uint64(11)) * (1.0 / 9007199254740992.0)


@njit(cache=True)
def run_chunk(
    n_steps,
    step0,
    v,
    g,
    until,
    rfc,
    bias,
    indptr,
    indices,
    wmv,
    ring,
    ring_cnt,
    delay,
    grp_idx,
    grp_start,
    grp_p,
    grp_log1mp,
    kick,
    rng,
    consts,
    out_idx,
    out_t,
):
    """Advance ``n_steps`` time steps; return the number of spikes produced.

    Spikes beyond the capacity of ``out_idx`` are still simulated but not
    recorded (the returned count then exceeds the capacity).
    """
    n = v.shape[0]
    n_slots = delay + 1
    v0 = consts[0]
    vrst = consts[1]
    vth = consts[2]
    em = consts[3]
    es = consts[4]
    acoef = consts[5]
    cap = out_idx.shape[0]
    n_out = 0
    spiked = np.empty(n, dtype=np.int32)
    n_grp = grp_p.shape[0]

    for s in range(n_steps):
        step = step0 + s
        ns = 0
        # 1. exact integration of v and g (frozen while refractory) + threshold
        for i in range(n):
            if step >= until[i]:
                rest = v0 + bias[i]
                gi = g[i]
                vi = rest + (v[i] - rest) * em + acoef * gi
                v[i] = vi
                g[i] = gi * es
                if vi > vth:
                    spiked[ns] = i
                    ns += 1
        # 2. Poisson kicks (drive on v, as brian2.PoissonInput): an independent
        #    Bernoulli(p) draw per target and step, sampled by geometric skipping
        for gi_ in range(n_grp):
            start = grp_start[gi_]
            m = grp_start[gi_ + 1] - start
            p = grp_p[gi_]
            lg = grp_log1mp[gi_]
            pos = -1
            while True:
                if p >= 1.0:
                    pos += 1
                else:
                    pos += 1 + int(np.log(1.0 - _u01(rng)) / lg)
                if pos >= m:
                    break
                i = grp_idx[start + pos]
                before = v[i]
                v[i] = before + kick
                if before <= vth and v[i] > vth and step >= until[i]:
                    spiked[ns] = i
                    ns += 1
        # 3. deliver synaptic events that are due now (ignored while refractory)
        slot = step % n_slots
        cnt = ring_cnt[slot]
        for q in range(cnt):
            j = ring[slot, q]
            for e in range(indptr[j], indptr[j + 1]):
                tgt = indices[e]
                if step >= until[tgt]:
                    g[tgt] += wmv[e]
        ring_cnt[slot] = 0
        # 4. reset and schedule outgoing events
        tslot = (step + delay) % n_slots
        for q in range(ns):
            i = spiked[q]
            v[i] = vrst
            g[i] = 0.0
            until[i] = step + rfc[i] + 1
            ring[tslot, ring_cnt[tslot]] = i
            ring_cnt[tslot] += 1
            if n_out < cap:
                out_idx[n_out] = i
                out_t[n_out] = s
            n_out += 1
    return n_out


def _splitmix64(seed: int) -> int:
    z = (seed + 0x9E3779B97F4A7C15) & 0xFFFFFFFFFFFFFFFF
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & 0xFFFFFFFFFFFFFFFF
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & 0xFFFFFFFFFFFFFFFF
    z ^= z >> 31
    return z or 0x1


# ---------------------------------------------------------------------------
# Simulator
# ---------------------------------------------------------------------------


@dataclass
class SpikeBatch:
    """Spikes produced by one :meth:`Simulator.run` call."""

    neurons: np.ndarray  # int32 neuron indices
    steps: np.ndarray  # int32 step offsets within the call
    n_steps: int
    truncated: bool = False

    @property
    def count(self) -> int:
        return int(self.neurons.shape[0])


@dataclass
class PerfStats:
    """Smoothed cost of the simulation kernel."""

    cpu_ms_per_bio_second: float = 0.0
    spikes_per_bio_second: float = 0.0
    total_cpu_s: float = 0.0
    total_bio_s: float = 0.0
    calls: int = 0

    def update(self, cpu_s: float, bio_s: float, spikes: int) -> None:
        if bio_s <= 0:
            return
        alpha = 0.2 if self.calls else 1.0
        ratio = cpu_s * 1000.0 / bio_s
        rate = spikes / bio_s
        self.cpu_ms_per_bio_second += alpha * (ratio - self.cpu_ms_per_bio_second)
        self.spikes_per_bio_second += alpha * (rate - self.spikes_per_bio_second)
        self.total_cpu_s += cpu_s
        self.total_bio_s += bio_s
        self.calls += 1


class Simulator:
    """One independent simulation state over a (shared, read-only) graph."""

    def __init__(
        self,
        graph: Graph,
        stim_groups: dict[str, np.ndarray] | None = None,
        params: LifParams = DEFAULT_PARAMS,
        seed: int = 783,
        shared_from: "Simulator | None" = None,
    ) -> None:
        self.graph = graph
        self.params = params
        self.n = graph.n
        self.lock = threading.RLock()
        self.stim_groups = {k: np.asarray(v, dtype=np.int32) for k, v in (stim_groups or {}).items()}
        if shared_from is not None and shared_from.graph is graph and shared_from.params == params:
            # share the (large, read-only) synapse arrays between simulators
            self._wmv = shared_from._wmv
            self._indptr = shared_from._indptr
            self._indices = shared_from._indices
        else:
            self._wmv = (graph.weights.astype(np.float32) * np.float32(params.w_syn)).astype(np.float32)
            self._indptr = np.ascontiguousarray(graph.indptr, dtype=np.int64)
            self._indices = np.ascontiguousarray(graph.indices, dtype=np.int32)
        self._consts = params.consts()
        self._delay = params.delay_steps
        self._ring = np.zeros((self._delay + 1, max(self.n, 1)), dtype=np.int32)
        self._ring_cnt = np.zeros(self._delay + 1, dtype=np.int64)
        self.v = np.full(self.n, params.v0, dtype=np.float64)
        self.g = np.zeros(self.n, dtype=np.float64)
        self.bias = np.zeros(self.n, dtype=np.float64)
        self.until = np.zeros(self.n, dtype=np.int64)  # first step at which a neuron is no longer refractory
        self.rfc = np.full(self.n, params.refractory_steps, dtype=np.int32)
        for idx in self.stim_groups.values():
            self.rfc[idx] = 0  # Poisson targets have no refractory period
        self._rng = np.zeros(1, dtype=np.uint64)
        self._set_groups([])
        self._rates: dict[str, float] = {}
        self.t_step = 0  # steps since the last reset (indexes the delay ring)
        self.steps_total = 0  # lifetime step counter (never reset)
        self.perf = PerfStats()
        self.seed(seed)

    def sibling(self, seed: int) -> "Simulator":
        """A new independent simulation state over the same graph."""
        return Simulator(self.graph, self.stim_groups, self.params, seed, shared_from=self)

    # -- state management --------------------------------------------------

    @property
    def dt(self) -> float:
        return self.params.dt

    @property
    def bio_ms(self) -> float:
        return self.steps_total * self.params.dt

    def seed(self, seed: int) -> None:
        self._rng[0] = np.uint64(_splitmix64(int(seed)))

    def reset(self, seed: int | None = None) -> None:
        """Return all neurons to rest and drop in-flight synaptic events."""
        with self.lock:
            self.v.fill(self.params.v0)
            self.g.fill(0.0)
            self.until.fill(0)
            self._ring_cnt.fill(0)
            self.t_step = 0
            if seed is not None:
                self.seed(seed)

    def _set_groups(self, groups: list[tuple[np.ndarray, float]]) -> None:
        """Install Poisson target groups ``(neuron indices, per-step probability)``."""
        if groups:
            self._grp_idx = np.concatenate([g[0] for g in groups]).astype(np.int32)
            sizes = np.array([g[0].shape[0] for g in groups], dtype=np.int64)
            self._grp_start = np.concatenate([[0], np.cumsum(sizes)]).astype(np.int64)
            self._grp_p = np.array([g[1] for g in groups], dtype=np.float64)
            self._grp_log1mp = np.array(
                [math.log1p(-g[1]) if g[1] < 1.0 else -math.inf for g in groups], dtype=np.float64
            )
        else:
            self._grp_idx = np.zeros(0, dtype=np.int32)
            self._grp_start = np.zeros(1, dtype=np.int64)
            self._grp_p = np.zeros(0, dtype=np.float64)
            self._grp_log1mp = np.zeros(0, dtype=np.float64)

    def set_rates(self, rates: dict[str, float]) -> None:
        """Set Poisson rates (Hz) per stimulation group; unknown keys are ignored."""
        with self.lock:
            groups: list[tuple[np.ndarray, float]] = []
            applied: dict[str, float] = {}
            for key, idx in self.stim_groups.items():
                rate = float(rates.get(key, 0.0))
                if rate > 0.0 and idx.shape[0]:
                    groups.append((idx, min(1.0, rate * self.params.dt / 1000.0)))
                    applied[key] = rate
            self._rates = applied
            self._set_groups(groups)

    @property
    def rates(self) -> dict[str, float]:
        return dict(self._rates)

    # -- running ------------------------------------------------------------

    def _capacity(self, n_steps: int) -> int:
        return int(min(self.n * n_steps // 8 + 4096, 6_000_000))

    def run(self, n_steps: int) -> SpikeBatch:
        """Advance ``n_steps`` steps with the current Poisson drive."""
        if n_steps <= 0:
            empty = np.zeros(0, dtype=np.int32)
            return SpikeBatch(empty, empty, 0)
        with self.lock:
            cap = self._capacity(n_steps)
            out_idx = np.empty(cap, dtype=np.int32)
            out_t = np.empty(cap, dtype=np.int32)
            cpu0 = time.thread_time()
            total = run_chunk(
                n_steps,
                self.t_step,
                self.v,
                self.g,
                self.until,
                self.rfc,
                self.bias,
                self._indptr,
                self._indices,
                self._wmv,
                self._ring,
                self._ring_cnt,
                self._delay,
                self._grp_idx,
                self._grp_start,
                self._grp_p,
                self._grp_log1mp,
                self.params.poisson_kick,
                self._rng,
                self._consts,
                out_idx,
                out_t,
            )
            cpu = time.thread_time() - cpu0
            self.t_step += n_steps
            self.steps_total += n_steps
            kept = int(min(total, cap))
            self.perf.update(cpu, n_steps * self.params.dt / 1000.0, int(total))
            return SpikeBatch(out_idx[:kept].copy(), out_t[:kept].copy(), n_steps, truncated=total > cap)

    def run_ms(self, duration_ms: float) -> SpikeBatch:
        return self.run(int(round(duration_ms / self.params.dt)))

    def run_trial(
        self,
        rates: dict[str, float],
        duration_ms: float,
        warmup_ms: float = 0.0,
        seed: int | None = None,
        chunk_ms: float = 100.0,
    ) -> np.ndarray:
        """Reset, drive with ``rates`` and return per-neuron spike counts of the
        measurement window (after ``warmup_ms``)."""
        counts = np.zeros(self.n, dtype=np.int64)
        with self.lock:
            self.reset(seed)
            self.set_rates(rates)
            chunk = max(1, int(round(chunk_ms / self.params.dt)))
            warm = int(round(warmup_ms / self.params.dt))
            while warm > 0:
                step = min(chunk, warm)
                self.run(step)
                warm -= step
            remaining = int(round(duration_ms / self.params.dt))
            while remaining > 0:
                step = min(chunk, remaining)
                batch = self.run(step)
                if batch.count:
                    counts += np.bincount(batch.neurons, minlength=self.n)
                remaining -= step
        return counts


def warm_up() -> None:
    """Compile the kernel (and populate numba's on-disk cache) with a tiny net."""
    graph = Graph(
        np.array([0, 1, 1], dtype=np.int64),
        np.array([1], dtype=np.int32),
        np.array([10.0], dtype=np.float32),
    )
    sim = Simulator(graph, {"x": np.array([0], dtype=np.int32)})
    sim.set_rates({"x": 100.0})
    sim.run(50)
