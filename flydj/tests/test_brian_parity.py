"""Optional parity check against Brian2 (skipped when Brian2 is not installed).

Runs the Shiu et al. equations in Brian2 and in this simulator on the same small
random network with the same Poisson drive and compares mean firing rates.
The two use different random number streams, so only statistics are compared.
"""

import numpy as np
import pytest

brian2 = pytest.importorskip("brian2")

from app.simulator import Graph, Simulator  # noqa: E402


def test_mean_rates_agree_with_brian2():
    from brian2 import Hz, NeuronGroup, PoissonInput, SpikeMonitor, Synapses, mV, ms, Network, defaultclock

    brian2.start_scope()
    rng = np.random.default_rng(2)
    n, n_in = 200, 20
    pre, post, w = [], [], []
    for i in range(n):
        for j in rng.choice(n, size=8, replace=False):
            if j != i:
                pre.append(i)
                post.append(int(j))
                w.append(float(rng.integers(8, 45) * (1 if rng.random() > 0.2 else -1)))
    pre, post, w = np.array(pre), np.array(post), np.array(w)
    duration_ms, rate = 2000.0, 100.0

    # ---- Brian2 reference (equations from Drosophila_brain_model/model.py)
    defaultclock.dt = 0.1 * ms
    eqs = """
    dv/dt = (v_0 - v + g) / t_mbr : volt (unless refractory)
    dg/dt = -g / tau               : volt (unless refractory)
    rfc                            : second
    """
    ns = dict(v_0=-52 * mV, v_rst=-52 * mV, v_th=-45 * mV, t_mbr=20 * ms, tau=5 * ms)
    neu = NeuronGroup(n, eqs, method="linear", threshold="v > v_th", reset="v = v_rst; g = 0 * mV",
                      refractory="rfc", namespace=ns)
    neu.v, neu.g, neu.rfc = -52 * mV, 0 * mV, 2.2 * ms
    syn = Synapses(neu, neu, "w : volt", on_pre="g += w", delay=1.8 * ms)
    syn.connect(i=pre, j=post)
    syn.w = w * 0.275 * mV
    pois = []
    for i in range(n_in):
        neu[i].rfc = 0 * ms
        pois.append(PoissonInput(neu[i], "v", 1, rate * Hz, weight=0.275 * 250 * mV))
    mon = SpikeMonitor(neu)
    net = Network(neu, syn, mon, *pois)
    net.run(duration_ms * ms)
    ref = np.bincount(np.asarray(mon.i), minlength=n) / (duration_ms / 1000.0)

    # ---- this simulator
    order = np.argsort(pre, kind="stable")
    indptr = np.zeros(n + 1, dtype=np.int64)
    np.cumsum(np.bincount(pre, minlength=n), out=indptr[1:])
    graph = Graph(indptr, post[order].astype(np.int32), w[order].astype(np.float32))
    sim = Simulator(graph, {"in": np.arange(n_in, dtype=np.int32)}, seed=11)
    ours = sim.run_trial({"in": rate}, duration_ms, seed=11) / (duration_ms / 1000.0)

    # Poisson targets fire once per input event in both simulators
    assert ours[:n_in].mean() == pytest.approx(rate, rel=0.06)
    assert ref[:n_in].mean() == pytest.approx(rate, rel=0.06)
    # downstream activity: same level and the same neurons respond
    assert ref[n_in:].mean() > 1.0  # the network is driven strongly enough to compare
    assert ours[n_in:].mean() == pytest.approx(ref[n_in:].mean(), rel=0.2)
    assert np.corrcoef(ours[n_in:], ref[n_in:])[0, 1] > 0.8
