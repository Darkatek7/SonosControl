import json
import math
import struct

import numpy as np
import pytest

from app.connectome import load_connectome, save_connectome
from app.stream import ViewerHub, decode_frame, decode_geometry, encode_frame, encode_geometry

from .conftest import make_model


def test_geometry_round_trip_and_layout():
    model = make_model()
    data = encode_geometry(model)
    assert data[:4] == b"FDJG"
    (length,) = struct.unpack_from("<I", data, 4)
    header = json.loads(data[8 : 8 + length])
    assert header["version"] == 1
    assert header["neuronCount"] == model.n_neurons
    assert header["silhouetteCount"] == model.silhouette.shape[0]
    assert header["cellTypes"] == model.cell_types
    assert [p["key"] for p in header["populations"]] == [p.key for p in model.populations]
    assert set(header["populations"][0]) == {"id", "key", "label", "role"}
    body = 8 + length + (-length) % 4
    assert body % 4 == 0
    expected = body + model.n_neurons * 12 + model.silhouette.shape[0] * 12 + model.n_neurons * 2 + model.n_neurons
    assert len(data) == expected
    decoded = decode_geometry(data)
    assert np.array_equal(decoded["positions"], model.positions)
    assert np.array_equal(decoded["silhouette"], model.silhouette)
    assert np.array_equal(decoded["cellTypeIndex"], model.cell_type_index)
    assert np.array_equal(decoded["population"], model.population)
    # padding bytes are zero
    assert data[8 + length : body] == b"\x00" * ((-length) % 4)


def test_geometry_is_little_endian_f32():
    model = make_model()
    data = encode_geometry(model)
    (length,) = struct.unpack_from("<I", data, 4)
    offset = 8 + length + (-length) % 4
    first = struct.unpack_from("<fff", data, offset)
    assert first == pytest.approx(tuple(float(v) for v in model.positions[0]))


def test_frame_round_trip_exact():
    rates = np.array([1.5, 0.0, 22.25, 3.0, 4.5, 9.75], dtype=np.float32)
    spikes = np.array([0, 5, 7999, 123456], dtype=np.uint32)
    frame = encode_frame(12345.678, 0.84, rates, spikes)
    assert len(frame) == 20 + 4 * 6 + 4 * 4
    assert frame[0] == 0xF1 and frame[1] == 1
    assert struct.unpack_from("<H", frame, 2)[0] == 6
    assert struct.unpack_from("<I", frame, 4)[0] == 4
    assert struct.unpack_from("<d", frame, 8)[0] == 12345.678
    out = decode_frame(frame)
    assert out["bioMs"] == 12345.678
    assert out["engagement"] == pytest.approx(0.84, rel=1e-6)
    assert np.array_equal(out["rates"], rates) and np.array_equal(out["spikes"], spikes)


def test_frame_without_engagement_or_spikes():
    frame = encode_frame(0.0, None, np.zeros(3, dtype=np.float32), None)
    out = decode_frame(frame)
    assert math.isnan(out["engagement"]) and out["spikes"].size == 0 and out["rates"].size == 3


def test_unknown_frames_are_rejected():
    frame = bytearray(encode_frame(0.0, None, np.zeros(1, dtype=np.float32), None))
    frame[1] = 2
    with pytest.raises(ValueError):
        decode_frame(bytes(frame))


def test_artifact_round_trip(tmp_path):
    model = make_model()
    path = tmp_path / "m.npz"
    save_connectome(path, model)
    loaded = load_connectome(path)
    assert np.array_equal(loaded.indptr, model.indptr) and np.array_equal(loaded.weights, model.weights)
    assert loaded.cell_types == model.cell_types and loaded.meta == model.meta
    assert [(p.id, p.key, p.role) for p in loaded.populations] == [(p.id, p.key, p.role) for p in model.populations]
    loaded.validate_required()


def test_viewer_hub_drops_oldest_and_reports_changes():
    import asyncio

    async def scenario():
        changes = []
        hub = ViewerHub(changes.append)
        a = hub.add()
        b = hub.add()
        for i in range(20):
            hub.broadcast(bytes([i]))
        assert a.queue.qsize() == 8 and a.queue.get_nowait() == bytes([12])
        hub.remove(a)
        hub.remove(a)
        hub.remove(b)
        assert changes == [1, 2, 1, 0]

    asyncio.run(scenario())
