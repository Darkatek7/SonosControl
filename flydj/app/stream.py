"""Binary encodings of the sidecar API (see ``API.md``) and viewer tracking.

Geometry (``GET /v1/model/geometry``), little-endian::

    "FDJG" | u32 H | H bytes UTF-8 JSON header | zero pad to 4 bytes
    f32[N*3] positions | f32[S*3] silhouette | u16[N] cell type | u8[N] population

Stream frame (``WS /v1/stream``), little-endian::

    u8 0xF1 | u8 1 | u16 P | u32 K | f64 bioMs | f32 engagement
    f32[P] population rates (Hz) | u32[K] neurons that spiked since the last frame
"""

from __future__ import annotations

import asyncio
import json
import struct
from typing import Callable

import numpy as np

from .connectome import Connectome

GEOMETRY_MAGIC = b"FDJG"
GEOMETRY_VERSION = 1
FRAME_MAGIC = 0xF1
FRAME_VERSION = 1
_FRAME_HEADER = struct.Struct("<BBHIdf")


def encode_geometry(model: Connectome) -> bytes:
    header = {
        "version": GEOMETRY_VERSION,
        "neuronCount": model.n_neurons,
        "silhouetteCount": int(model.silhouette.shape[0]),
        "populations": [
            {"id": p.id, "key": p.key, "label": p.label, "role": p.role} for p in model.populations
        ],
        "cellTypes": model.cell_types,
    }
    blob = json.dumps(header, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    pad = (-len(blob)) % 4
    out = bytearray()
    out += GEOMETRY_MAGIC
    out += struct.pack("<I", len(blob))
    out += blob
    out += b"\x00" * pad
    out += np.ascontiguousarray(model.positions, dtype="<f4").tobytes()
    out += np.ascontiguousarray(model.silhouette, dtype="<f4").tobytes()
    out += np.ascontiguousarray(model.cell_type_index, dtype="<u2").tobytes()
    out += np.ascontiguousarray(model.population, dtype="u1").tobytes()
    return bytes(out)


def decode_geometry(data: bytes) -> dict:
    """Inverse of :func:`encode_geometry` (used by tests and diagnostics)."""
    if data[:4] != GEOMETRY_MAGIC:
        raise ValueError("bad geometry magic")
    (length,) = struct.unpack_from("<I", data, 4)
    header = json.loads(data[8 : 8 + length].decode("utf-8"))
    offset = 8 + length + ((-length) % 4)
    n = header["neuronCount"]
    s = header["silhouetteCount"]
    positions = np.frombuffer(data, dtype="<f4", count=n * 3, offset=offset).reshape(n, 3)
    offset += n * 12
    silhouette = np.frombuffer(data, dtype="<f4", count=s * 3, offset=offset).reshape(s, 3)
    offset += s * 12
    cell_types = np.frombuffer(data, dtype="<u2", count=n, offset=offset)
    offset += n * 2
    population = np.frombuffer(data, dtype="u1", count=n, offset=offset)
    offset += n
    if offset != len(data):
        raise ValueError("trailing bytes in geometry")
    return {
        "header": header,
        "positions": positions,
        "silhouette": silhouette,
        "cellTypeIndex": cell_types,
        "population": population,
    }


def encode_frame(
    bio_ms: float, engagement: float | None, rates: np.ndarray | list[float], spikes: np.ndarray | None
) -> bytes:
    rates = np.ascontiguousarray(rates, dtype="<f4")
    spikes = np.zeros(0, dtype="<u4") if spikes is None else np.ascontiguousarray(spikes, dtype="<u4")
    eng = float("nan") if engagement is None else float(engagement)
    header = _FRAME_HEADER.pack(FRAME_MAGIC, FRAME_VERSION, rates.shape[0], spikes.shape[0], float(bio_ms), eng)
    return header + rates.tobytes() + spikes.tobytes()


def decode_frame(data: bytes) -> dict:
    magic, version, p, k, bio_ms, engagement = _FRAME_HEADER.unpack_from(data, 0)
    if magic != FRAME_MAGIC or version != FRAME_VERSION:
        raise ValueError("unsupported frame")
    offset = _FRAME_HEADER.size
    rates = np.frombuffer(data, dtype="<f4", count=p, offset=offset)
    offset += 4 * p
    spikes = np.frombuffer(data, dtype="<u4", count=k, offset=offset)
    if offset + 4 * k != len(data):
        raise ValueError("bad frame length")
    return {"bioMs": bio_ms, "engagement": engagement, "rates": rates, "spikes": spikes}


class Viewer:
    """One connected stream client with a small drop-oldest queue."""

    def __init__(self, maxsize: int = 8) -> None:
        self.queue: asyncio.Queue[bytes] = asyncio.Queue(maxsize=maxsize)

    def push(self, frame: bytes) -> None:
        if self.queue.full():
            try:
                self.queue.get_nowait()
            except asyncio.QueueEmpty:  # pragma: no cover - race-free on one loop
                pass
        self.queue.put_nowait(frame)


class ViewerHub:
    """Tracks stream viewers and fans frames out to them (event-loop only)."""

    def __init__(self, on_change: Callable[[int], None] | None = None) -> None:
        self._viewers: set[Viewer] = set()
        self._on_change = on_change

    @property
    def count(self) -> int:
        return len(self._viewers)

    def add(self) -> Viewer:
        viewer = Viewer()
        self._viewers.add(viewer)
        if self._on_change:
            self._on_change(self.count)
        return viewer

    def remove(self, viewer: Viewer) -> None:
        if viewer in self._viewers:
            self._viewers.discard(viewer)
            if self._on_change:
                self._on_change(self.count)

    def broadcast(self, frame: bytes) -> None:
        for viewer in tuple(self._viewers):
            viewer.push(frame)
