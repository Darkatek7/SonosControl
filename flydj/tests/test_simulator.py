import math
import threading

import numpy as np
import pytest

from app.simulator import DEFAULT_PARAMS as P
from app.simulator import Graph, Simulator, shuffled_graph


def empty_graph(n: int) -> Graph:
    return Graph(np.zeros(n + 1, dtype=np.int64), np.zeros(0, dtype=np.int32), np.zeros(0, dtype=np.float32))


def graph_from_edges(n: int, edges: list[tuple[int, int, float]]) -> Graph:
    edges = sorted(edges)
    indptr = np.zeros(n + 1, dtype=np.int64)
    for pre, _, _ in edges:
        indptr[pre + 1] += 1
    indptr = np.cumsum(indptr)
    return Graph(
        indptr,
        np.array([e[1] for e in edges], dtype=np.int32),
        np.array([e[2] for e in edges], dtype=np.float32),
    )


def test_parameters_match_shiu_et_al():
    assert (P.v0, P.v_rst, P.v_th) == (-52.0, -52.0, -45.0)
    assert (P.t_mbr, P.tau, P.t_rfc, P.t_dly) == (20.0, 5.0, 2.2, 1.8)
    assert P.w_syn == 0.275 and P.dt == 0.1
    assert P.poisson_kick == pytest.approx(0.275 * 250)
    assert P.delay_steps == 18 and P.refractory_steps == 21


@pytest.mark.parametrize("t_ms", [5, 10, 25, 50, 100])
def test_subthreshold_response_matches_analytic_solution(t_ms):
    sim = Simulator(empty_graph(1))
    g0 = 3.0
    sim.g[0] = g0
    sim.run(int(t_ms / P.dt))
    coupling = P.tau / (P.tau - P.t_mbr) * g0
    expected = coupling * (math.exp(-t_ms / P.tau) - math.exp(-t_ms / P.t_mbr))
    assert sim.v[0] - P.v0 == pytest.approx(expected, rel=1e-9, abs=1e-12)
    assert sim.g[0] == pytest.approx(g0 * math.exp(-t_ms / P.tau), rel=1e-9)


def test_constant_drive_firing_rate_matches_lif_formula():
    sim = Simulator(empty_graph(1))
    sim.bias[0] = 10.0  # steady offset (test hook): V_inf = -42 mV
    v_inf = P.v0 + 10.0
    isi = P.t_mbr * math.log((v_inf - P.v_rst) / (v_inf - P.v_th)) + P.t_rfc
    expected_hz = 1000.0 / isi
    batch = sim.run_ms(4000)
    measured_hz = batch.count / 4.0
    assert measured_hz == pytest.approx(expected_hz, rel=0.04)


def test_spike_reaches_postsynaptic_neuron_after_1_8_ms():
    sim = Simulator(graph_from_edges(2, [(0, 1, 100.0)]))
    sim.v[0] = P.v_th + 1.0  # neuron 0 crosses threshold in the first step
    sim.run(18)  # steps 0..17: the event is still in flight
    assert sim.g[1] == 0.0
    sim.run(1)  # step 18 = 1.8 ms after the spike step
    assert sim.g[1] == pytest.approx(100.0 * P.w_syn)
    assert sim.v[1] == pytest.approx(P.v0)  # affects v from the next step on
    sim.run(1)
    assert sim.v[1] > P.v0


def test_refractory_period_blocks_second_spike():
    sim = Simulator(empty_graph(1))
    sim.bias[0] = 40.0  # would spike every step without refractoriness
    batch = sim.run_ms(100)
    gaps = np.diff(batch.steps)
    assert gaps.min() >= P.refractory_steps + 1


def test_poisson_drive_produces_spikes_at_the_requested_rate():
    sim = Simulator(empty_graph(50), {"x": np.arange(50, dtype=np.int32)}, seed=3)
    sim.set_rates({"x": 100.0})
    batch = sim.run_ms(2000)
    assert batch.count / 50 / 2.0 == pytest.approx(100.0, rel=0.08)
    sim.reset(3)
    sim.set_rates({})
    assert sim.run_ms(200).count == 0


def test_same_seed_is_deterministic_and_seed_matters():
    graph = graph_from_edges(30, [(i, (i * 7 + 3) % 30, 20.0) for i in range(30)] + [(i, (i + 1) % 30, 40.0) for i in range(30)])
    groups = {"x": np.arange(5, dtype=np.int32)}

    def run(seed: int):
        sim = Simulator(graph, groups, seed=seed)
        sim.set_rates({"x": 150.0})
        batch = sim.run_ms(300)
        return batch.neurons.copy(), batch.steps.copy()

    a, b, c = run(1), run(1), run(2)
    assert np.array_equal(a[0], b[0]) and np.array_equal(a[1], b[1])
    assert a[0].shape != c[0].shape or not np.array_equal(a[1], c[1])


def test_results_do_not_depend_on_the_calling_thread():
    graph = graph_from_edges(10, [(i, (i + 1) % 10, 30.0) for i in range(10)])
    groups = {"x": np.array([0], dtype=np.int32)}
    results = []

    def work():
        sim = Simulator(graph, groups, seed=9)
        sim.set_rates({"x": 120.0})
        results.append(sim.run_ms(200).steps.copy())

    work()
    thread = threading.Thread(target=work)
    thread.start()
    thread.join()
    assert np.array_equal(results[0], results[1])


def test_inhibitory_weights_reduce_firing():
    def downstream_spikes(weight: float) -> int:
        sim = Simulator(graph_from_edges(2, [(0, 1, weight)]), {"x": np.array([0], dtype=np.int32)}, seed=4)
        sim.bias[1] = 5.5  # just below threshold: V_inf = -46.5 mV
        sim.set_rates({"x": 200.0})
        batch = sim.run_ms(1000)
        return int((batch.neurons == 1).sum())

    base = downstream_spikes(0.0)
    excited = downstream_spikes(15.0)
    inhibited = downstream_spikes(-15.0)
    assert excited > base
    assert inhibited < base or (base == 0 and inhibited == 0)
    assert inhibited < excited


def test_shuffle_preserves_degrees_and_weights():
    graph = graph_from_edges(40, [(i, (i * 3 + k) % 40, float(k + 1)) for i in range(40) for k in range(5)])
    shuffled = shuffled_graph(graph, 1)
    assert np.array_equal(shuffled.indptr, graph.indptr)
    assert np.array_equal(shuffled.weights, graph.weights)
    assert np.array_equal(np.bincount(shuffled.indices, minlength=40), np.bincount(graph.indices, minlength=40))
    assert not np.array_equal(shuffled.indices, graph.indices)


def test_chunking_equals_one_long_run():
    graph = graph_from_edges(20, [(i, (i + 1) % 20, 35.0) for i in range(20)])
    groups = {"x": np.array([0, 5], dtype=np.int32)}
    one = Simulator(graph, groups, seed=8)
    one.set_rates({"x": 150.0})
    a = one.run(2000)
    two = Simulator(graph, groups, seed=8)
    two.set_rates({"x": 150.0})
    first = two.run(700)
    second = two.run(1300)
    merged = np.concatenate([first.steps, second.steps + 700])
    assert np.array_equal(a.steps, merged)
    assert np.array_equal(a.neurons, np.concatenate([first.neurons, second.neurons]))


def test_synaptic_events_are_ignored_while_the_target_is_refractory():
    """Brian2 semantics of ``(unless refractory)``: g is not writable then."""
    sim = Simulator(graph_from_edges(2, [(0, 1, 100.0)]))
    sim.v[0] = P.v_th + 1.0  # neuron 0 spikes at step 0; its event is due at step 18
    sim.run(3)
    sim.v[1] = P.v_th + 1.0  # neuron 1 spikes at step 3: refractory for steps 4..24
    sim.run(16)  # steps 3..18 (the event arrives at step 18)
    assert sim.g[1] == 0.0
    sim.run(40)  # neuron 1 is free again afterwards, but the event is gone
    assert sim.g[1] == 0.0 and abs(sim.v[1] - P.v0) < 1e-9
