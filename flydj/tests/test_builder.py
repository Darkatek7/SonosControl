"""Builder tests on a synthetic dataset (no network)."""

import csv
import re

import numpy as np
import pytest

pytest.importorskip("yaml")
from tools import build_connectome as b  # noqa: E402


def make_graph():
    """60 neurons: seeds 0-14, chain seeds -> 20..24 -> 30..34 -> readouts 50..53,
    a weak decoy path through 40..44, and filler neurons near the seeds."""
    n = 60
    edges: dict[tuple[int, int], float] = {}
    for s in range(15):
        edges[(s, 20 + s % 5)] = 12.0
        edges[(s, 40 + s % 5)] = 5.0  # weaker decoy branch
    for m in range(5):
        edges[(20 + m, 30 + m)] = 30.0
        edges[(30 + m, 50 + m % 4)] = 25.0
        edges[(40 + m, 45 + m)] = 5.0
    edges[(45, 55)] = 5.0
    for f in range(56, 60):  # filler reachable at 1 hop with strong input
        edges[(0, f)] = 8.0 + f
    pre = np.array([e[0] for e in edges], dtype=np.int32)
    post = np.array([e[1] for e in edges], dtype=np.int32)
    w = np.array(list(edges.values()), dtype=np.float32)
    w[(pre == 3) & (post == 20 + 3 % 5)] *= -1  # one inhibitory edge
    return n, pre, post, w


def test_select_subgraph_keeps_seeds_readouts_and_strong_paths():
    n, pre, post, w = make_graph()
    seeds = np.arange(15)
    readouts = {"approach:A": np.array([50, 51]), "startle:B": np.array([52]), "arousal:C": np.array([53])}
    sel = b.select_subgraph(n, pre, post, w, seeds, readouts, max_neurons=40, paths_per_group=10)
    keep = set(sel.keep.tolist())
    assert set(range(15)) <= keep and {50, 51, 52, 53} <= keep
    assert set(range(20, 25)) <= keep and set(range(30, 35)) <= keep  # strongest chain
    assert len(keep) <= 40
    assert sel.report["groups"]["approach:A"]["hops"] == 3
    assert list(sel.keep) == sorted(sel.keep)


def test_select_subgraph_respects_the_cap_and_ranks_by_input():
    n, pre, post, w = make_graph()
    seeds = np.arange(15)
    readouts = {"approach:A": np.array([50]), "startle:B": np.array([52]), "arousal:C": np.array([53])}
    must = 15 + 3
    sel = b.select_subgraph(n, pre, post, w, seeds, readouts, max_neurons=must + 6, paths_per_group=3)
    assert len(sel.keep) == must + 6
    # filler 59 receives the strongest input (8 + 59) from seed 0 and beats weak decoys
    assert 59 in set(sel.keep.tolist())


def test_select_subgraph_fails_when_seeds_exceed_budget():
    n, pre, post, w = make_graph()
    with pytest.raises(b.BuildError):
        b.select_subgraph(n, pre, post, w, np.arange(15), {"x": np.array([50])}, max_neurons=10)


def test_bfs_levels_and_gather():
    indptr = np.array([0, 2, 3, 3, 3], dtype=np.int64)
    indices = np.array([1, 2, 3], dtype=np.int32)
    dist = b.bfs_levels(indptr, indices, np.array([0]))
    assert dist.tolist() == [0, 1, 1, 2]
    offsets, owner = b.gather_rows(indptr, np.array([0, 1]))
    assert offsets.tolist() == [0, 1, 2] and owner.tolist() == [0, 0, 1]


# ------------------------------------------------------------- full pipeline

READOUTS_YAML = """
version: 1
populations:
  jo_a: {id: 1, role: input, label: JO-A, cell_types: ['JO-A\\d+']}
  jo_b: {id: 2, role: input, label: JO-B, cell_types: ['JO-B\\d+(_[a-z])?']}
  jo_ce: {id: 3, role: input, label: JO-CE, cell_types: ['JO-C\\w+']}
  approach: {id: 4, role: output, label: Approach, cell_types: ['pC1[a-e]'], fru_dsx: [fru, dsx], min_direct_input: 5}
  startle: {id: 5, role: output, label: Startle, cell_types: ['DNp01']}
  arousal: {id: 6, role: output, label: Arousal, cell_types: ['AMMC-A1']}
"""


def write_dataset(tmp_path, drop_type=None):
    pa = pytest.importorskip("pyarrow")
    pq = pytest.importorskip("pyarrow.parquet")
    n, pre, post, w = make_graph()
    ids = np.arange(n, dtype=np.int64) + 720575940000000000
    types = ["X"] * n
    for i in range(5):
        types[i] = "JO-A1"
        types[5 + i] = "JO-B1_a"
        types[10 + i] = "JO-CA1"
    types[50], types[51], types[52], types[53] = "pC1a", "pC1b", "DNp01", "AMMC-A1"
    types[56] = "Z"  # fru-positive neuron receiving >= 5 synapses from the seeds
    if drop_type:
        types = ["X" if t == drop_type else t for t in types]
    comp = tmp_path / "Completeness_783.csv"
    with open(comp, "w", newline="") as f:
        wr = csv.writer(f)
        wr.writerow(["", "Completed"])
        for r in ids:
            wr.writerow([int(r), True])
    tsv = tmp_path / "Supplemental_file1_neuron_annotations.tsv"
    with open(tsv, "w", newline="") as f:
        wr = csv.writer(f, delimiter="\t")
        wr.writerow(["root_id", "pos_x", "pos_y", "pos_z", "soma_x", "soma_y", "soma_z", "cell_type", "fru_dsx", "synonyms", "super_class"])
        rng = np.random.default_rng(0)
        for i, r in enumerate(ids):
            pos = rng.integers(1000, 5000, size=3)
            soma = ("", "", "") if i < 15 else tuple(pos + 10)
            fru = "fru" if i == 56 else ""
            wr.writerow([int(r), *pos, *soma, types[i], fru, "", "central"])
    con = tmp_path / "Connectivity_783.parquet"
    pq.write_table(
        pa.table(
            {
                "Presynaptic_Index": pre.astype(np.int64),
                "Postsynaptic_Index": post.astype(np.int64),
                "Connectivity": np.abs(w).astype(np.int64),
                "Excitatory x Connectivity": w.astype(np.int64),
            }
        ),
        con,
    )
    yml = tmp_path / "readouts.yaml"
    yml.write_text(READOUTS_YAML)
    return {"completeness": comp, "annotations": tsv, "connectivity": con}, yml


def test_build_auditory_artifact_end_to_end(tmp_path):
    paths, yml = write_dataset(tmp_path)
    model, report = b.build_connectome("auditory", paths, yml, max_neurons=30)
    assert model.n_neurons <= 30
    assert {p.key: model.population_size(p.key) for p in model.populations} == {
        "jo_a": 5, "jo_b": 5, "jo_ce": 5, "approach": 3, "startle": 1, "arousal": 1,
    }  # approach = pC1a, pC1b and the rule-based fru neuron 56
    model.validate_required()
    assert model.meta["dataVersion"] == "flywire-783" and model.meta["scope"] == "auditory"
    # inhibitory sign survives
    assert (model.weights < 0).any()
    # centred on the centroid, +y dorsal: silhouette and neurons share the frame
    assert model.silhouette.shape[0] == 60
    assert np.isfinite(model.positions).all() and np.isfinite(model.silhouette).all()
    assert model.cell_type_index.max() < len(model.cell_types)
    assert report["artifact"]["neurons"] == model.n_neurons
    # root ids line up with the retained rows
    assert np.all(np.diff(model.root_ids) > 0)


def test_build_full_scope_keeps_everything(tmp_path):
    paths, yml = write_dataset(tmp_path)
    model, _ = b.build_connectome("full", paths, yml)
    n, pre, post, w = make_graph()
    assert model.n_neurons == n and model.n_connections == len(pre)
    assert model.synapse_count == int(np.abs(w).sum())
    assert model.positions.shape == (n, 3)
    allpos = model.positions.astype(np.float64)
    assert np.allclose(allpos.mean(axis=0), 0.0, atol=1e-3)


def test_axes_are_rotated_so_y_is_dorsal(tmp_path):
    paths, yml = write_dataset(tmp_path)
    ann = b.read_annotations(paths["annotations"], b.read_completeness(paths["completeness"]))
    pos, valid = b.neuron_positions(ann)
    raw = np.where(np.isfinite(ann.soma).all(axis=1)[:, None], ann.soma, ann.anchor) * b.VOXEL_NM / 1000
    assert valid.all()
    centred = raw - raw.mean(axis=0)
    assert np.allclose(pos[:, 0], centred[:, 0], atol=1e-3)
    assert np.allclose(pos[:, 1], -centred[:, 1], atol=1e-3)
    assert np.allclose(pos[:, 2], -centred[:, 2], atol=1e-3)


def test_build_fails_clearly_when_a_population_is_empty(tmp_path):
    paths, yml = write_dataset(tmp_path, drop_type="DNp01")
    with pytest.raises(b.BuildError, match="population 'startle' is empty"):
        b.build_connectome("auditory", paths, yml, max_neurons=30)


def test_pinned_sources_are_fully_pinned():
    for spec in b.PINNED_SOURCES.values():
        assert re.fullmatch(r"[0-9a-f]{64}", spec["sha256"])
        assert re.search(r"/[0-9a-f]{40}/", spec["url"]) and spec["url"].startswith("https://raw.githubusercontent.com/")


def test_offline_mode_requires_verified_files(tmp_path):
    with pytest.raises(b.BuildError, match="offline"):
        b.ensure_sources(tmp_path, offline=True)
    bad = tmp_path / b.PINNED_SOURCES["completeness"]["filename"]
    bad.write_text("tampered")
    with pytest.raises(b.BuildError):
        b.ensure_sources(tmp_path, offline=True)


def test_download_rejects_non_https(tmp_path):
    with pytest.raises(b.BuildError):
        b.download("http://example.com/x", tmp_path / "x", "0" * 64, 10)
