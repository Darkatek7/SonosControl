"""Connectome artifact: an in-memory CSR graph plus labels and geometry.

The artifact is a compressed ``.npz`` written by ``tools/build_connectome.py``.
Only plain numeric arrays and JSON strings are stored, so loading never needs
``allow_pickle``.

Arrays (``N`` neurons, ``E`` connections, ``S`` silhouette points):

``indptr`` int64[N+1], ``indices`` int32[E], ``weights`` float32[E]
    Compressed sparse rows by presynaptic neuron. ``weights`` holds the signed
    synapse count (the "Excitatory x Connectivity" column of the Shiu et al.
    release), positive for excitatory and negative for inhibitory.
``root_ids`` int64[N]
    FlyWire root ids.
``population`` uint8[N]
    Population id per neuron, ``0`` meaning "not in a named population".
``cell_type_index`` uint16[N]
    Index into ``cell_types`` (``65535`` = unknown).
``positions`` float32[N, 3], ``silhouette`` float32[S, 3]
    Micrometres, centred on the brain centroid. +y is dorsal and +z anterior.
``populations_json``, ``cell_types_json``, ``meta_json``
    JSON strings.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

UNKNOWN_CELL_TYPE = 65535
ROLE_INPUT = "input"
ROLE_OUTPUT = "output"
ROLE_OTHER = "other"

REQUIRED_INPUTS = ("jo_a", "jo_b", "jo_ce")
REQUIRED_OUTPUTS = ("approach", "startle", "arousal")


@dataclass(frozen=True)
class Population:
    id: int
    key: str
    label: str
    role: str


@dataclass
class Connectome:
    indptr: np.ndarray
    indices: np.ndarray
    weights: np.ndarray
    root_ids: np.ndarray
    population: np.ndarray
    cell_type_index: np.ndarray
    cell_types: list[str]
    positions: np.ndarray
    silhouette: np.ndarray
    populations: list[Population]
    meta: dict = field(default_factory=dict)

    def __post_init__(self) -> None:
        self.populations = sorted(self.populations, key=lambda p: p.id)
        self._by_key = {p.key: p for p in self.populations}
        self._index_cache: dict[str, np.ndarray] = {}
        n = self.n_neurons
        if self.indptr.shape != (n + 1,):
            raise ValueError("indptr must have N+1 entries")
        if self.indices.shape != self.weights.shape:
            raise ValueError("indices and weights must have the same length")
        if int(self.indptr[-1]) != self.indices.shape[0]:
            raise ValueError("indptr does not match the number of connections")
        for arr, name in ((self.population, "population"), (self.cell_type_index, "cell_type_index")):
            if arr.shape != (n,):
                raise ValueError(f"{name} must have N entries")
        if self.positions.shape != (n, 3):
            raise ValueError("positions must be N x 3")

    @property
    def n_neurons(self) -> int:
        return int(self.root_ids.shape[0])

    @property
    def n_connections(self) -> int:
        return int(self.indices.shape[0])

    @property
    def synapse_count(self) -> int:
        return int(np.abs(self.weights).sum(dtype=np.float64))

    def population_by_key(self, key: str) -> Population:
        return self._by_key[key]

    def has_population(self, key: str) -> bool:
        return key in self._by_key

    def population_indices(self, key: str) -> np.ndarray:
        """Neuron indices (int32) belonging to the population ``key``."""
        cached = self._index_cache.get(key)
        if cached is None:
            pid = self._by_key[key].id
            cached = np.flatnonzero(self.population == pid).astype(np.int32)
            self._index_cache[key] = cached
        return cached

    def population_size(self, key: str) -> int:
        return int(self.population_indices(key).shape[0])

    def input_keys(self) -> list[str]:
        return [p.key for p in self.populations if p.role == ROLE_INPUT]

    def output_keys(self) -> list[str]:
        return [p.key for p in self.populations if p.role == ROLE_OUTPUT]

    def validate_required(self) -> None:
        missing = [k for k in REQUIRED_INPUTS + REQUIRED_OUTPUTS if not self.has_population(k)]
        if missing:
            raise ValueError(f"artifact lacks required populations: {', '.join(missing)}")
        empty = [p.key for p in self.populations if self.population_size(p.key) == 0]
        if empty:
            raise ValueError(f"artifact has empty populations: {', '.join(empty)}")

    def describe_populations(self) -> list[dict]:
        return [
            {
                "id": p.id,
                "key": p.key,
                "label": p.label,
                "role": p.role,
                "neuronCount": self.population_size(p.key),
            }
            for p in self.populations
        ]


def _json_array(value: object) -> np.ndarray:
    return np.array(json.dumps(value, separators=(",", ":"), ensure_ascii=False))


def save_connectome(path: Path | str, model: Connectome) -> None:
    """Write ``model`` as a compressed npz artifact (atomically)."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    populations = [
        {"id": p.id, "key": p.key, "label": p.label, "role": p.role} for p in model.populations
    ]
    with open(tmp, "wb") as handle:
        np.savez_compressed(
            handle,
            indptr=model.indptr.astype(np.int64),
            indices=model.indices.astype(np.int32),
            weights=model.weights.astype(np.float32),
            root_ids=model.root_ids.astype(np.int64),
            population=model.population.astype(np.uint8),
            cell_type_index=model.cell_type_index.astype(np.uint16),
            positions=model.positions.astype(np.float32),
            silhouette=model.silhouette.astype(np.float32),
            populations_json=_json_array(populations),
            cell_types_json=_json_array(model.cell_types),
            meta_json=_json_array(model.meta),
        )
    tmp.replace(path)


def load_connectome(path: Path | str) -> Connectome:
    """Load an artifact written by :func:`save_connectome`."""
    with np.load(str(path), allow_pickle=False) as data:
        populations = [
            Population(int(p["id"]), str(p["key"]), str(p["label"]), str(p["role"]))
            for p in json.loads(str(data["populations_json"]))
        ]
        model = Connectome(
            indptr=np.ascontiguousarray(data["indptr"], dtype=np.int64),
            indices=np.ascontiguousarray(data["indices"], dtype=np.int32),
            weights=np.ascontiguousarray(data["weights"], dtype=np.float32),
            root_ids=np.ascontiguousarray(data["root_ids"], dtype=np.int64),
            population=np.ascontiguousarray(data["population"], dtype=np.uint8),
            cell_type_index=np.ascontiguousarray(data["cell_type_index"], dtype=np.uint16),
            cell_types=list(json.loads(str(data["cell_types_json"]))),
            positions=np.ascontiguousarray(data["positions"], dtype=np.float32),
            silhouette=np.ascontiguousarray(data["silhouette"], dtype=np.float32),
            populations=populations,
            meta=dict(json.loads(str(data["meta_json"]))),
        )
    return model
