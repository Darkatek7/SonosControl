#!/usr/bin/env python3
"""Build the FlyDJ connectome artifact from public FlyWire data.

Inputs (downloaded once, pinned by commit and verified by SHA-256):

* ``Connectivity_783.parquet`` and ``Completeness_783.csv`` from
  https://github.com/philshiu/Drosophila_brain_model (MIT) - the FlyWire
  release 783 edge list with signed synapse counts (data: CC BY 4.0).
* ``Supplemental_file1_neuron_annotations.tsv`` from
  https://github.com/flyconnectome/flywire_annotations - cell types, soma
  positions and neurotransmitter predictions (Schlegel et al. 2024).

Output: a compressed ``.npz`` (see ``app/connectome.py``).

``--scope auditory`` keeps a subgraph: the Johnston's organ seeds, the output
populations, the neurons on the strongest shortest paths from seeds to
readouts and the best-connected neurons within two hops of the seeds, capped at
``--max-neurons``. ``--scope full`` keeps every neuron.

The module only needs numpy, pyyaml and (to read the edge list) pyarrow, so the
service can import it to build the full connectome on first start. Raw
downloads are treated as untrusted data and read with pyarrow/csv only.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import sys
import time
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import numpy as np

REPO_ROOT = Path(__file__).resolve().parent.parent
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from app.connectome import (  # noqa: E402
    ROLE_INPUT,
    ROLE_OUTPUT,
    UNKNOWN_CELL_TYPE,
    Connectome,
    Population,
    save_connectome,
)

DATA_VERSION = "flywire-783"
BUILDER_VERSION = 1

_SHIU_COMMIT = "91bdd1e7dcf193f3e7ca5a8933497fcef63b7960"
_ANNOT_COMMIT = "a83b2776d60d5764cef36b927f5f9679c16c47a2"

PINNED_SOURCES: dict[str, dict] = {
    "connectivity": {
        "filename": "Connectivity_783.parquet",
        "url": f"https://raw.githubusercontent.com/philshiu/Drosophila_brain_model/{_SHIU_COMMIT}/Connectivity_783.parquet",
        "sha256": "efeb23fb99098e9c390f6869969b2a121a2ee92c833cfc45ecb2c1d8e1af0347",
        "size": 100804642,
    },
    "completeness": {
        "filename": "Completeness_783.csv",
        "url": f"https://raw.githubusercontent.com/philshiu/Drosophila_brain_model/{_SHIU_COMMIT}/Completeness_783.csv",
        "sha256": "bbb847a4cc2caaa7a16349722d220c087317b946d148d4d592d94d250617a311",
        "size": 3327347,
    },
    "annotations": {
        "filename": "Supplemental_file1_neuron_annotations.tsv",
        "url": f"https://raw.githubusercontent.com/flyconnectome/flywire_annotations/{_ANNOT_COMMIT}/supplemental_files/Supplemental_file1_neuron_annotations.tsv",
        "sha256": "b214970b55d2fbe0853bba536fdcb9e28730f4eb7ab06f600491df795da683cd",
        "size": 31720298,
    },
}

# Voxel size of FlyWire coordinates in nanometres.
VOXEL_NM = np.array([4.0, 4.0, 40.0])
SILHOUETTE_POINTS = 30000
SILHOUETTE_SEED = 783

ProgressFn = Callable[[float, str], None]


class BuildError(RuntimeError):
    """Raised for problems the operator has to fix (bad data, empty population)."""


def _noop(_fraction: float, _message: str) -> None:
    return None


# ---------------------------------------------------------------------------
# Download
# ---------------------------------------------------------------------------


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(
    url: str,
    dest: Path,
    sha256: str,
    max_bytes: int,
    on_bytes: Callable[[int, int], None] | None = None,
) -> None:
    """Download ``url`` to ``dest`` and verify its SHA-256 (https only)."""
    if not url.startswith("https://"):
        raise BuildError(f"refusing non-https download: {url}")
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_name(dest.name + ".part")
    request = urllib.request.Request(url, headers={"User-Agent": "flydj-builder/1"})
    digest = hashlib.sha256()
    total = 0
    with urllib.request.urlopen(request, timeout=60) as response, open(tmp, "wb") as out:  # noqa: S310
        length = int(response.headers.get("Content-Length") or 0)
        while True:
            block = response.read(1 << 20)
            if not block:
                break
            total += len(block)
            if total > max_bytes:
                raise BuildError(f"download of {url} exceeds {max_bytes} bytes")
            digest.update(block)
            out.write(block)
            if on_bytes:
                on_bytes(total, length)
    if digest.hexdigest() != sha256:
        tmp.unlink(missing_ok=True)
        raise BuildError(f"SHA-256 mismatch for {url}: got {digest.hexdigest()}, expected {sha256}")
    tmp.replace(dest)


def ensure_sources(data_dir: Path, progress: ProgressFn = _noop, offline: bool = False) -> dict[str, Path]:
    """Return local paths of the three pinned inputs, downloading if needed."""
    paths: dict[str, Path] = {}
    names = list(PINNED_SOURCES)
    for i, name in enumerate(names):
        spec = PINNED_SOURCES[name]
        path = data_dir / spec["filename"]
        paths[name] = path
        base = 0.0 + 0.30 * i / len(names)
        span = 0.30 / len(names)
        if path.exists() and sha256_file(path) == spec["sha256"]:
            progress(base + span, f"using verified {spec['filename']}")
            continue
        if offline:
            raise BuildError(f"{path} is missing or does not match its pinned SHA-256 (offline mode)")
        progress(base, f"downloading {spec['filename']}")

        def on_bytes(done: int, length: int, base=base, span=span, fname=spec["filename"]) -> None:
            if length:
                progress(base + span * done / length, f"downloading {fname}")

        download(spec["url"], path, spec["sha256"], int(spec["size"] * 1.2) + (1 << 20), on_bytes)
        progress(base + span, f"downloaded {spec['filename']}")
    return paths


# ---------------------------------------------------------------------------
# Readers
# ---------------------------------------------------------------------------


def read_completeness(path: Path) -> np.ndarray:
    """Root ids in the order used by the ``*_Index`` columns of the edge list."""
    ids: list[int] = []
    with open(path, newline="", encoding="utf-8") as handle:
        reader = csv.reader(handle)
        header = next(reader)
        if len(header) < 2 or header[1] != "Completed":
            raise BuildError(f"unexpected header in {path.name}: {header!r}")
        for row in reader:
            if row:
                ids.append(int(row[0]))
    return np.asarray(ids, dtype=np.int64)


def read_connectivity(path: Path, n_neurons: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Return ``(pre, post, signed_weight)`` of the edge list.

    ``signed_weight`` is the "Excitatory x Connectivity" column: the synapse
    count, negative where the presynaptic neuron is predicted inhibitory.
    """
    try:
        import pyarrow.parquet as pq
    except ImportError as exc:  # pragma: no cover - environment dependent
        raise BuildError("pyarrow is required to read Connectivity_783.parquet") from exc
    columns = ("Presynaptic_Index", "Postsynaptic_Index", "Excitatory x Connectivity")
    names = set(pq.ParquetFile(str(path)).schema_arrow.names)
    missing = [c for c in columns if c not in names]
    if missing:
        raise BuildError(f"{path.name} lacks expected columns {missing}; found {sorted(names)}")
    arrays = []
    for column in columns:
        arrays.append(pq.read_table(str(path), columns=[column]).column(column).to_numpy())
    pre = arrays[0].astype(np.int32)
    post = arrays[1].astype(np.int32)
    weight = arrays[2].astype(np.float32)
    if pre.min() < 0 or post.min() < 0 or pre.max() >= n_neurons or post.max() >= n_neurons:
        raise BuildError("edge list indices fall outside the completeness table")
    return pre, post, weight


@dataclass
class Annotations:
    cell_type: list[str]
    synonyms: list[str]
    fru_dsx: list[str]
    soma: np.ndarray  # (N, 3) voxels, NaN when missing
    anchor: np.ndarray  # (N, 3) voxels, NaN when missing
    found: int = 0


def read_annotations(path: Path, root_ids: np.ndarray) -> Annotations:
    """Read the annotation TSV and align it with ``root_ids``."""
    n = root_ids.shape[0]
    index_of = {int(r): i for i, r in enumerate(root_ids)}
    cell_type = [""] * n
    synonyms = [""] * n
    fru_dsx = [""] * n
    soma = np.full((n, 3), np.nan)
    anchor = np.full((n, 3), np.nan)
    found = 0
    csv.field_size_limit(1 << 24)
    with open(path, newline="", encoding="utf-8") as handle:
        reader = csv.reader(handle, delimiter="\t")
        header = next(reader)
        col = {name: i for i, name in enumerate(header)}
        need = ["root_id", "cell_type", "soma_x", "soma_y", "soma_z", "pos_x", "pos_y", "pos_z"]
        missing = [c for c in need if c not in col]
        if missing:
            raise BuildError(f"{path.name} lacks expected columns {missing}")
        syn_col = col.get("synonyms")
        fd_col = col.get("fru_dsx")

        def floats(row: list[str], names: tuple[str, str, str]) -> list[float]:
            out = []
            for name in names:
                cell = row[col[name]] if col[name] < len(row) else ""
                try:
                    out.append(float(cell))
                except ValueError:
                    out.append(float("nan"))
            return out

        seen: set[int] = set()
        for row in reader:
            if not row:
                continue
            try:
                root = int(row[col["root_id"]])
            except ValueError:
                continue
            i = index_of.get(root)
            if i is None or root in seen:
                continue
            seen.add(root)
            found += 1
            cell_type[i] = row[col["cell_type"]].strip() if col["cell_type"] < len(row) else ""
            if syn_col is not None and syn_col < len(row):
                synonyms[i] = row[syn_col].strip()
            if fd_col is not None and fd_col < len(row):
                fru_dsx[i] = row[fd_col].strip()
            soma[i] = floats(row, ("soma_x", "soma_y", "soma_z"))
            anchor[i] = floats(row, ("pos_x", "pos_y", "pos_z"))
    return Annotations(cell_type, synonyms, fru_dsx, soma, anchor, found)


def neuron_positions(ann: Annotations) -> tuple[np.ndarray, np.ndarray]:
    """Micrometre positions centred on the brain centroid.

    The soma position is used when present, otherwise the anchor point on the
    neuron's backbone (sensory neurons have their soma outside the brain). The
    FlyWire frame has y pointing ventrally and z posteriorly, so the frame is
    rotated by 180 degrees about x (y -> -y, z -> -z) to make +y dorsal and +z
    anterior without mirroring the brain.
    """
    soma_ok = np.isfinite(ann.soma).all(axis=1)
    raw = np.where(soma_ok[:, None], ann.soma, ann.anchor)
    um = raw * VOXEL_NM / 1000.0
    um[:, 1] *= -1.0
    um[:, 2] *= -1.0
    valid = np.isfinite(um).all(axis=1)
    if not valid.any():
        raise BuildError("no neuron has a usable position")
    centroid = um[valid].mean(axis=0)
    um = um - centroid
    um[~valid] = 0.0
    return um.astype(np.float32), valid


# ---------------------------------------------------------------------------
# Populations
# ---------------------------------------------------------------------------


@dataclass
class PopulationSpec:
    key: str
    id: int
    role: str
    label: str
    cell_types: list[str] = field(default_factory=list)
    synonyms: list[str] = field(default_factory=list)
    # rule-based membership (applied after all curated matches, to unassigned
    # neurons only): annotated as one of ``fru_dsx`` and receiving at least
    # ``min_direct_input`` synapses from the input populations
    fru_dsx: list[str] = field(default_factory=list)
    min_direct_input: int = 5


def load_readouts(path: Path) -> list[PopulationSpec]:
    import yaml  # local import: only needed when building

    with open(path, encoding="utf-8") as handle:
        doc = yaml.safe_load(handle)
    specs: list[PopulationSpec] = []
    for key, body in (doc.get("populations") or {}).items():
        specs.append(
            PopulationSpec(
                key=str(key),
                id=int(body["id"]),
                role=str(body["role"]),
                label=str(body.get("label", key)),
                cell_types=[str(p) for p in body.get("cell_types", [])],
                synonyms=[str(s) for s in body.get("synonyms", [])],
                fru_dsx=[str(s) for s in body.get("fru_dsx", [])],
                min_direct_input=int(body.get("min_direct_input", 5)),
            )
        )
    ids = [s.id for s in specs]
    if len(set(ids)) != len(ids) or any(i < 1 or i > 255 for i in ids):
        raise BuildError("population ids must be unique and within 1..255")
    for required in ("jo_a", "jo_b", "jo_ce", "approach", "startle", "arousal"):
        if required not in {s.key for s in specs}:
            raise BuildError(f"readouts file lacks required population '{required}'")
    return sorted(specs, key=lambda s: s.id)


def assign_populations(
    specs: list[PopulationSpec],
    cell_types: list[str],
    synonyms: list[str],
    fru_dsx: list[str] | None = None,
    direct_input: Callable[[np.ndarray], np.ndarray] | None = None,
) -> tuple[np.ndarray, dict[str, dict[str, int]]]:
    """Return the population id per neuron and per-type match counts.

    Curated matches (cell type regexes, synonym substrings) are assigned first,
    in ascending id order. Rule-based populations (``fru_dsx`` plus a minimum of
    direct synapses from the input populations, computed by ``direct_input``)
    then take only neurons that are still unassigned. Raises
    :class:`BuildError` when a population matches no neuron.
    """
    n = len(cell_types)
    population = np.zeros(n, dtype=np.uint8)
    ct_array = np.asarray(cell_types, dtype=object)
    syn_array = np.asarray(synonyms, dtype=object)
    for spec in specs:
        mask = np.zeros(n, dtype=bool)
        for pattern in spec.cell_types:
            rx = re.compile(pattern)
            mask |= np.fromiter((bool(rx.fullmatch(c)) for c in ct_array), dtype=bool, count=n)
        for needle in spec.synonyms:
            mask |= np.fromiter((needle in s for s in syn_array), dtype=bool, count=n)
        mask &= population == 0
        population[mask] = spec.id
    rule_specs = [s for s in specs if s.fru_dsx]
    if rule_specs:
        if fru_dsx is None or direct_input is None:
            raise BuildError("rule-based populations need fru/dsx annotations and edge data")
        flags = np.asarray(fru_dsx, dtype=object)
        inputs = direct_input(population)
        for spec in rule_specs:
            mask = np.isin(flags, spec.fru_dsx) & (inputs >= spec.min_direct_input) & (population == 0)
            population[mask] = spec.id
    report: dict[str, dict[str, int]] = {}
    for spec in specs:
        mask = population == spec.id
        if not mask.any():
            raise BuildError(
                f"population '{spec.key}' is empty: none of {spec.cell_types + spec.synonyms} "
                "matches a cell type in the annotations"
            )
        counts: dict[str, int] = {}
        for c in ct_array[mask]:
            counts[c or "?"] = counts.get(c or "?", 0) + 1
        report[spec.key] = dict(sorted(counts.items(), key=lambda kv: -kv[1]))
    return population, report


# ---------------------------------------------------------------------------
# Graph helpers
# ---------------------------------------------------------------------------


def make_csr(n: int, src: np.ndarray, dst: np.ndarray, val: np.ndarray):
    order = np.argsort(src, kind="stable")
    indptr = np.zeros(n + 1, dtype=np.int64)
    np.cumsum(np.bincount(src, minlength=n), out=indptr[1:])
    return indptr, dst[order], val[order]


def gather_rows(indptr: np.ndarray, nodes: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Edge offsets of all rows in ``nodes`` and the position of each owner."""
    starts = indptr[nodes]
    lens = indptr[nodes + 1] - starts
    total = int(lens.sum())
    if total == 0:
        return np.zeros(0, dtype=np.int64), np.zeros(0, dtype=np.int64)
    owner = np.repeat(np.arange(nodes.shape[0]), lens)
    offsets = np.arange(total) - np.repeat(np.cumsum(lens) - lens, lens) + np.repeat(starts, lens)
    return offsets, owner


def bfs_levels(indptr: np.ndarray, indices: np.ndarray, sources: np.ndarray, max_depth: int | None = None):
    """Breadth-first hop distance (int16, -1 = unreached) from ``sources``."""
    n = indptr.shape[0] - 1
    dist = np.full(n, -1, dtype=np.int16)
    frontier = np.unique(sources.astype(np.int64))
    dist[frontier] = 0
    depth = 0
    while frontier.size and (max_depth is None or depth < max_depth):
        offsets, _ = gather_rows(indptr, frontier)
        if offsets.size == 0:
            break
        nxt = indices[offsets]
        nxt = np.unique(nxt[dist[nxt] < 0])
        depth += 1
        dist[nxt] = depth
        frontier = nxt.astype(np.int64)
    return dist


@dataclass
class Selection:
    keep: np.ndarray  # sorted neuron indices
    report: dict


def _layered_best_strength(
    indptr: np.ndarray,
    indices: np.ndarray,
    counts: np.ndarray,
    in_total: np.ndarray,
    dist: np.ndarray,
    start_nodes: np.ndarray,
    max_layer: int,
    forward_from: np.ndarray | None = None,
) -> np.ndarray:
    """Max-product path strength along hop-shortest edges (layer d -> d+1).

    Edge strength is the fraction of the target's synaptic input that the edge
    carries. ``start_nodes`` get strength 1 and layers are processed upward from
    layer 0 (forward DP). ``forward_from`` optionally restricts the nodes that
    may carry strength.
    """
    n = indptr.shape[0] - 1
    strength = np.zeros(n)
    strength[start_nodes] = 1.0
    order_by_layer = [np.flatnonzero(dist == d) for d in range(max_layer + 1)]
    for d in range(max_layer):
        nodes = order_by_layer[d]
        if forward_from is not None:
            nodes = nodes[forward_from[nodes]]
        if nodes.size == 0:
            continue
        offsets, owner = gather_rows(indptr, nodes)
        if offsets.size == 0:
            continue
        dst = indices[offsets]
        ok = dist[dst] == d + 1
        if forward_from is not None:
            ok &= forward_from[dst]
        if not ok.any():
            continue
        offsets, owner, dst = offsets[ok], owner[ok], dst[ok]
        frac = counts[offsets] / np.maximum(in_total[dst], 1.0)
        np.maximum.at(strength, dst, strength[nodes[owner]] * frac)
    return strength


def select_subgraph(
    n: int,
    pre: np.ndarray,
    post: np.ndarray,
    weight: np.ndarray,
    seeds: np.ndarray,
    readout_groups: dict[str, np.ndarray],
    max_neurons: int = 8000,
    path_min_syn: int = 3,
    bfs_min_syn: int = 5,
    hops: int = 2,
    paths_per_group: int = 100,
    progress: ProgressFn = _noop,
) -> Selection:
    """Choose the neurons of the auditory subgraph.

    ``readout_groups`` maps a label (``population:cell_type``) to the neuron
    indices of that group. The result always contains ``seeds`` and every
    readout neuron; the remaining budget is spent on

    1. neurons on the *strongest hop-shortest paths* from the seeds to each
       readout group (path strength = product over edges of the share of the
       target's input carried by the edge; top ``paths_per_group`` per group), and
    2. neurons within ``hops`` hops of the seeds over edges with at least
       ``bfs_min_syn`` synapses, added in rounds ranked by their summed input
       weight from the already selected set.
    """
    seeds = np.unique(seeds.astype(np.int64))
    readouts = np.unique(np.concatenate(list(readout_groups.values())).astype(np.int64))
    must = np.unique(np.concatenate([seeds, readouts]))
    if must.size > max_neurons:
        raise BuildError(f"{must.size} seed and readout neurons exceed max_neurons={max_neurons}")
    counts_all = np.abs(weight).astype(np.float32)
    in_total = np.bincount(post, weights=counts_all, minlength=n)

    # forward and reverse CSR of the edges used for path finding
    keep_edges = counts_all >= path_min_syn
    f_ptr, f_idx, f_cnt = make_csr(n, pre[keep_edges], post[keep_edges], counts_all[keep_edges])
    r_ptr, r_idx, _ = make_csr(n, post[keep_edges], pre[keep_edges], counts_all[keep_edges])
    progress(0.0, "computing hop distances from the seeds")
    dist_f = bfs_levels(f_ptr, f_idx, seeds)
    max_layer = int(dist_f.max())
    best_f = _layered_best_strength(f_ptr, f_idx, f_cnt, in_total, dist_f, seeds, max_layer)

    report: dict = {"groups": {}, "path_neurons": 0, "fill_neurons": 0}
    path_scores: dict[int, float] = {}
    for gi, (label, targets) in enumerate(sorted(readout_groups.items())):
        progress(0.1 + 0.4 * gi / max(1, len(readout_groups)), f"paths to {label}")
        targets = np.unique(targets.astype(np.int64))
        reachable = targets[dist_f[targets] >= 0]
        if reachable.size == 0:
            report["groups"][label] = {"neurons": int(targets.size), "reached": 0, "hops": None, "path_neurons": 0}
            continue
        depth = int(dist_f[reachable].min())
        goal = reachable[dist_f[reachable] == depth]
        dist_b = bfs_levels(r_ptr, r_idx, goal, max_depth=depth)
        on_path = (dist_f >= 0) & (dist_b >= 0) & (dist_f.astype(np.int32) + dist_b.astype(np.int32) == depth)
        # backward DP over the hop-shortest DAG restricted to on-path nodes
        best_b = np.zeros(n)
        best_b[goal] = 1.0
        layers = [np.flatnonzero(on_path & (dist_f == d)) for d in range(depth + 1)]
        for d in range(depth - 1, -1, -1):
            nodes = layers[d]
            if nodes.size == 0:
                continue
            offsets, owner = gather_rows(f_ptr, nodes)
            if offsets.size == 0:
                continue
            dst = f_idx[offsets]
            ok = on_path[dst] & (dist_f[dst] == d + 1)
            offsets, owner, dst = offsets[ok], owner[ok], dst[ok]
            frac = f_cnt[offsets] / np.maximum(in_total[dst], 1.0)
            np.maximum.at(best_b, nodes[owner], best_b[dst] * frac)
        nodes = np.flatnonzero(on_path)
        score = best_f[nodes] * best_b[nodes]
        top = np.argsort(-score, kind="stable")[:paths_per_group]
        for node, s in zip(nodes[top], score[top]):
            if s > 0:
                path_scores[int(node)] = max(path_scores.get(int(node), 0.0), float(s))
        report["groups"][label] = {
            "neurons": int(targets.size),
            "reached": int(reachable.size),
            "hops": depth,
            "path_neurons": int(min(top.size, nodes.size)),
        }

    selected = np.zeros(n, dtype=bool)
    selected[must] = True
    budget = max_neurons - int(selected.sum())
    path_nodes = np.array(sorted(path_scores, key=lambda k: -path_scores[k]), dtype=np.int64)
    path_nodes = path_nodes[~selected[path_nodes]][: int(budget * 0.4)]
    selected[path_nodes] = True
    report["path_neurons"] = int(path_nodes.size)

    # 2-hop BFS fill ranked by input weight from the selected set
    progress(0.55, "ranking neurons near the seeds")
    b_keep = counts_all >= bfs_min_syn
    b_ptr, b_idx, _ = make_csr(n, pre[b_keep], post[b_keep], counts_all[b_keep])
    dist_b2 = bfs_levels(b_ptr, b_idx, seeds, max_depth=hops)
    candidate = (dist_b2 > 0) & ~selected
    report["candidates"] = int(candidate.sum())
    full_ptr, full_idx, full_cnt = make_csr(n, pre, post, counts_all)
    rounds = 8
    for r in range(rounds):
        remaining = max_neurons - int(selected.sum())
        if remaining <= 0 or not candidate.any():
            break
        take = max(1, int(np.ceil(remaining / (rounds - r))))
        src = np.flatnonzero(selected).astype(np.int64)
        offsets, owner = gather_rows(full_ptr, src)
        dst = full_idx[offsets]
        w = full_cnt[offsets]
        ok = candidate[dst]
        score = np.bincount(dst[ok], weights=w[ok], minlength=n)
        pool = np.flatnonzero(candidate & (score > 0))
        if pool.size == 0:
            break
        top = pool[np.argsort(-score[pool], kind="stable")[:take]]
        selected[top] = True
        candidate[top] = False
        report["fill_neurons"] += int(top.size)
        progress(0.55 + 0.1 * (r + 1) / rounds, "adding neurons")
    return Selection(np.flatnonzero(selected), report)


# ---------------------------------------------------------------------------
# Artifact assembly
# ---------------------------------------------------------------------------


def _slice_graph(n: int, pre, post, weight, keep: np.ndarray):
    """Induced subgraph on ``keep`` (sorted old indices), re-indexed from 0."""
    new_index = np.full(n, -1, dtype=np.int64)
    new_index[keep] = np.arange(keep.shape[0])
    mask = (new_index[pre] >= 0) & (new_index[post] >= 0)
    src = new_index[pre[mask]].astype(np.int32)
    dst = new_index[post[mask]].astype(np.int32)
    return make_csr(keep.shape[0], src, dst, weight[mask].astype(np.float32))


def build_connectome(
    scope: str,
    paths: dict[str, Path],
    readouts_path: Path,
    max_neurons: int = 8000,
    progress: ProgressFn = _noop,
) -> tuple[Connectome, dict]:
    """Assemble the artifact in memory. Returns the model and a build report."""
    if scope not in ("auditory", "full"):
        raise BuildError(f"unknown scope {scope!r}")
    started = time.time()
    progress(0.30, "reading neuron list")
    root_ids = read_completeness(paths["completeness"])
    n = root_ids.shape[0]
    progress(0.32, "reading annotations")
    ann = read_annotations(paths["annotations"], root_ids)
    specs = load_readouts(readouts_path)
    progress(0.38, "reading edge list")
    pre, post, weight = read_connectivity(paths["connectivity"], n)
    input_ids = {s.id for s in specs if s.role == ROLE_INPUT}

    def direct_input(assigned: np.ndarray) -> np.ndarray:
        from_inputs = np.isin(assigned, list(input_ids))[pre]
        return np.bincount(post[from_inputs], weights=np.abs(weight[from_inputs]), minlength=n)

    population, pop_report = assign_populations(specs, ann.cell_type, ann.synonyms, ann.fru_dsx, direct_input)
    total_synapses = int(np.abs(weight).sum(dtype=np.float64))
    positions, valid = neuron_positions(ann)

    report: dict = {
        "scope": scope,
        "neurons_total": int(n),
        "connections_total": int(pre.shape[0]),
        "synapses_total": total_synapses,
        "annotated": ann.found,
        "populations": pop_report,
    }

    # silhouette: a fixed random sample of the whole brain in the same frame
    rng = np.random.default_rng(SILHOUETTE_SEED)
    pool = np.flatnonzero(valid)
    pick = np.sort(rng.choice(pool, size=min(SILHOUETTE_POINTS, pool.shape[0]), replace=False))
    silhouette = positions[pick].copy()

    if scope == "full":
        keep = np.arange(n)
        progress(0.6, "building compressed sparse rows")
        indptr, indices, weights = make_csr(n, pre, post, weight)
        report["selection"] = {"mode": "full"}
    else:
        seeds = np.flatnonzero(np.isin(population, [s.id for s in specs if s.role == ROLE_INPUT]))
        groups: dict[str, np.ndarray] = {}
        for spec in (s for s in specs if s.role == ROLE_OUTPUT):
            members = np.flatnonzero(population == spec.id)
            by_type: dict[str, list[int]] = {}
            for i in members:
                by_type.setdefault(ann.cell_type[i] or "?", []).append(int(i))
            for ctype, idx in by_type.items():
                groups[f"{spec.key}:{ctype}"] = np.asarray(idx)
        selection = select_subgraph(
            n,
            pre,
            post,
            weight,
            seeds,
            groups,
            max_neurons=max_neurons,
            progress=lambda f, m: progress(0.4 + 0.3 * f, m),
        )
        keep = selection.keep
        report["selection"] = {"mode": "auditory", "max_neurons": max_neurons, **selection.report}
        progress(0.75, "extracting the subgraph")
        indptr, indices, weights = _slice_graph(n, pre, post, weight, keep)

    # cell type table over the kept neurons
    kept_types = [ann.cell_type[i] for i in keep]
    table = sorted({t for t in kept_types if t})
    if len(table) >= UNKNOWN_CELL_TYPE:
        raise BuildError("too many distinct cell types for a uint16 index")
    lookup = {t: i for i, t in enumerate(table)}
    cell_type_index = np.array([lookup.get(t, UNKNOWN_CELL_TYPE) for t in kept_types], dtype=np.uint16)

    populations = [Population(s.id, s.key, s.label, s.role) for s in specs]
    model = Connectome(
        indptr=indptr,
        indices=indices.astype(np.int32),
        weights=weights.astype(np.float32),
        root_ids=root_ids[keep],
        population=population[keep],
        cell_type_index=cell_type_index,
        cell_types=table,
        positions=positions[keep],
        silhouette=silhouette,
        populations=populations,
        meta={
            "dataVersion": DATA_VERSION,
            "scope": scope,
            "totalSynapseCount": total_synapses,
            "totalNeuronCount": int(n),
            "builderVersion": BUILDER_VERSION,
            "sources": {
                k: {"url": v["url"], "sha256": v["sha256"]} for k, v in PINNED_SOURCES.items()
            },
            "frame": "micrometres, centred on centroid, +y dorsal, +z anterior",
        },
    )
    model.validate_required()
    report["artifact"] = {
        "neurons": model.n_neurons,
        "connections": model.n_connections,
        "synapses": model.synapse_count,
        "populations": {p.key: model.population_size(p.key) for p in model.populations},
    }
    report["build_seconds"] = round(time.time() - started, 1)
    return model, report


def seed_input_report(model: Connectome) -> dict[str, dict[str, float]]:
    """Share of each output population's input that comes directly from the
    input populations (evidence that the readouts are reachable)."""
    n = model.n_neurons
    seed_mask = np.isin(model.population, [p.id for p in model.populations if p.role == ROLE_INPUT])
    pre = np.repeat(np.arange(n), np.diff(model.indptr))
    w = np.abs(model.weights).astype(np.float64)
    total_in = np.bincount(model.indices, weights=w, minlength=n)
    direct = np.bincount(model.indices[seed_mask[pre]], weights=w[seed_mask[pre]], minlength=n)
    out: dict[str, dict[str, float]] = {}
    for p in model.populations:
        if p.role != ROLE_OUTPUT:
            continue
        idx = model.population_indices(p.key)
        denom = total_in[idx].sum()
        out[p.key] = {
            "direct_seed_input_fraction": float(direct[idx].sum() / denom) if denom else 0.0,
            "neurons_with_direct_seed_input": int((direct[idx] > 0).sum()),
        }
    return out


def build(
    scope: str,
    data_dir: Path,
    out_path: Path,
    readouts_path: Path | None = None,
    max_neurons: int = 8000,
    progress: ProgressFn = _noop,
    offline: bool = False,
) -> dict:
    """Download (if needed), build and write the artifact; return the report."""
    readouts_path = readouts_path or (Path(__file__).resolve().parent / "readouts.yaml")
    paths = ensure_sources(data_dir, progress, offline=offline)
    model, report = build_connectome(scope, paths, readouts_path, max_neurons, progress)
    progress(0.9, "writing the artifact")
    save_connectome(out_path, model)
    report["artifact"]["file_bytes"] = Path(out_path).stat().st_size
    report["seed_input"] = seed_input_report(model)
    progress(1.0, "done")
    return report


def _print_report(report: dict) -> None:
    print(f"scope: {report['scope']}  data: {DATA_VERSION}")
    print(
        f"whole brain: {report['neurons_total']} neurons, {report['connections_total']} connections, "
        f"{report['synapses_total']} synapses ({report['annotated']} annotated)"
    )
    for key, types in report["populations"].items():
        total = sum(types.values())
        shown = ", ".join(f"{t} x{c}" for t, c in list(types.items())[:12])
        more = " ..." if len(types) > 12 else ""
        print(f"  {key}: {total} neurons - {shown}{more}")
    sel = report.get("selection", {})
    if sel.get("groups"):
        print("path groups (hops from seeds):")
        for label, g in sel["groups"].items():
            print(f"  {label}: {g['reached']}/{g['neurons']} reachable, hops={g['hops']}, path neurons={g['path_neurons']}")
        print(
            f"selection: {sel['path_neurons']} path neurons, {sel['fill_neurons']} from the {sel['hops'] if 'hops' in sel else 2}-hop "
            f"fill ({sel['candidates']} candidates)"
        )
    art = report["artifact"]
    print(
        f"artifact: {art['neurons']} neurons, {art['connections']} connections, {art['synapses']} synapses, "
        f"{art.get('file_bytes', 0) / 1e6:.2f} MB, populations {art['populations']}"
    )
    for key, info in report.get("seed_input", {}).items():
        print(
            f"  {key}: direct JO input {100 * info['direct_seed_input_fraction']:.2f}% of its input, "
            f"{info['neurons_with_direct_seed_input']} neurons with direct JO input"
        )
    print(f"build time: {report['build_seconds']} s")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--scope", choices=("auditory", "full"), default="auditory")
    parser.add_argument("--max-neurons", type=int, default=8000)
    parser.add_argument("--data-dir", type=Path, default=Path("artifacts/flydj-data"))
    parser.add_argument("--out", type=Path, default=None, help="output .npz (default: <data-dir>/connectome_<scope>.npz)")
    parser.add_argument("--readouts", type=Path, default=Path(__file__).resolve().parent / "readouts.yaml")
    parser.add_argument("--offline", action="store_true", help="never download; require verified local files")
    parser.add_argument("--keep-raw", action="store_true", help="keep downloads (default for local runs)")
    args = parser.parse_args(argv)
    out = args.out or args.data_dir / f"connectome_{args.scope}.npz"

    def progress(fraction: float, message: str) -> None:
        print(f"[{100 * fraction:5.1f}%] {message}", flush=True)

    try:
        report = build(args.scope, args.data_dir, out, args.readouts, args.max_neurons, progress, args.offline)
    except BuildError as exc:
        print(f"BUILD FAILED: {exc}", file=sys.stderr)
        return 2
    _print_report(report)
    print(json.dumps({"out": str(out)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
