"""HTTP and WebSocket contract tests against a fixture model (no network)."""

import time

import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.audio import AudioError
from app.engine import Engine
from app.main import create_app
from app.stream import decode_frame, decode_geometry

from .conftest import fake_features, make_model, make_settings, wait_ready

OUTPUTS = ["approach", "startle", "arousal"]


def make_client(model_path, tmp_path, secret="", sampler=None):
    settings = make_settings(model_path, tmp_path, shared_secret=secret)
    engine = Engine(settings, audio_sampler=sampler or default_sampler)
    app = create_app(settings, engine)
    client = TestClient(app, raise_server_exceptions=True)
    client._flydj_headers = {"X-FlyDj-Token": secret} if secret else None
    return client, engine


def default_sampler(url: str):
    if "bad" in url:
        raise AudioError("unsupported_stream", "nope")
    if "loud" in url:
        return fake_features(loudness=1.0, low=0.2, mid=0.2, high=0.6, onset=5.0)
    return fake_features()


@pytest.fixture()
def api(fixture_model_path, tmp_path):
    client, engine = make_client(fixture_model_path, tmp_path)
    with client:
        wait_ready(client)
        yield client, engine


# ----------------------------------------------------------------- basics


def test_health_model_and_state(api):
    client, _ = api
    assert client.get("/health").json() == {"status": "ok", "modelStatus": "ready"}
    doc = client.get("/v1/model").json()
    assert doc["dataVersion"] == "flywire-783" and doc["scope"] == "auditory" and doc["status"] == "ready"
    assert doc["buildProgress"] == 1.0 and doc["message"] is None
    assert doc["neuronCount"] == make_model().n_neurons and doc["connectionCount"] > 0 and doc["synapseCount"] > 0
    keys = {p["key"]: p for p in doc["populations"]}
    assert {"jo_a", "jo_b", "jo_ce"} <= keys.keys() and set(OUTPUTS) <= keys.keys()
    assert keys["jo_a"]["role"] == "input" and keys["approach"]["role"] == "output"
    assert all(1 <= p["id"] <= 255 and p["neuronCount"] > 0 for p in doc["populations"])
    assert doc["attribution"] and all({"text", "url"} <= a.keys() for a in doc["attribution"])
    state = client.get("/v1/state").json()
    assert state["mode"] == "stopped" and state["running"] is False and state["viewers"] == 0
    assert set(state) == {
        "mode", "running", "viewers", "bioTimeMs", "realtimeFactor", "cpuMsPerBioSecond",
        "spikesPerSecond", "engagement", "populationRates", "listeningUrl", "lastFeatures", "modelStatus",
    }


def test_shuffle_control_appears_eventually(api):
    client, _ = api
    deadline = time.time() + 30
    while time.time() < deadline:
        control = client.get("/v1/model").json()["shuffleControl"]
        if control:
            break
        time.sleep(0.1)
    assert control and {"realResponse", "shuffledResponse", "ratio", "trials"} <= control.keys()


def test_unknown_route_and_method_use_error_shape(api):
    client, _ = api
    r = client.get("/nope")
    assert r.status_code == 404 and r.json()["error"] == "not_found" and "message" in r.json()
    r = client.get("/v1/audition")
    assert r.status_code == 405 and r.json()["error"] == "method_not_allowed"


def test_geometry_binary_and_etag(api):
    client, _ = api
    r = client.get("/v1/model/geometry")
    assert r.status_code == 200 and r.headers["content-type"] == "application/octet-stream"
    etag = r.headers["etag"]
    assert etag.startswith('"') and not etag.startswith("W/")
    geo = decode_geometry(r.content)
    model = make_model()
    assert geo["header"]["neuronCount"] == model.n_neurons and np.array_equal(geo["positions"], model.positions)
    cached = client.get("/v1/model/geometry", headers={"If-None-Match": etag})
    assert cached.status_code == 304 and cached.content == b""
    assert client.get("/v1/model/geometry", headers={"If-None-Match": '"other"'}).status_code == 200


def test_start_stop_are_idempotent(api):
    client, _ = api
    first = client.post("/v1/run/start").json()
    second = client.post("/v1/run/start").json()
    assert first["running"] and second["running"] and second["mode"] == "bursting"
    stopped = client.post("/v1/run/stop").json()
    again = client.post("/v1/run/stop").json()
    assert not stopped["running"] and stopped["mode"] == "stopped" and again["mode"] == "stopped"


# ------------------------------------------------------- audition and listen


def test_audition_and_listen_return_409_while_stopped(api):
    client, _ = api
    r = client.post("/v1/audition", json={"candidates": [{"id": "a", "url": "x.example/loud"}]})
    assert r.status_code == 409 and r.json()["error"] == "not_running"
    r = client.post("/v1/listen", json={"url": "x.example/loud"})
    assert r.status_code == 409 and r.json()["error"] == "not_running"


def test_audition_scores_candidates(api):
    client, _ = api
    client.post("/v1/run/start")
    body = {
        "candidates": [
            {"id": "st-1", "url": "stream.example.com/loud.mp3"},
            {"id": "st-2", "url": "stream.example.com/calm.mp3"},
            {"id": "st-3", "url": "stream.example.com/bad"},
        ],
        "bioMs": 300,
    }
    r = client.post("/v1/audition", json=body)
    assert r.status_code == 200
    data = r.json()
    assert data["bioMs"] == 300 and isinstance(data["bioMs"], int) and set(data["baseline"]) == set(OUTPUTS)
    by_id = {x["id"]: x for x in data["results"]}
    assert [x["id"] for x in data["results"]] == ["st-1", "st-2", "st-3"]
    for cid in ("st-1", "st-2"):
        res = by_id[cid]
        assert res["ok"] and res["error"] is None and isinstance(res["score"], float)
        assert set(res["populationRates"]) == set(OUTPUTS)
        assert set(res["features"]) == {"low", "mid", "high", "loudness", "onsetRate", "centroidHz"}
    assert by_id["st-3"] == {
        "id": "st-3", "ok": False, "error": "unsupported_stream", "score": None, "features": None, "populationRates": None,
    }
    assert any(v > 0 for v in by_id["st-1"]["populationRates"].values())
    # deterministic: the same request gives the same scores
    again = client.post("/v1/audition", json=body).json()
    assert [x["score"] for x in again["results"]] == [x["score"] for x in data["results"]]


def test_audition_validation(api):
    client, _ = api
    client.post("/v1/run/start")
    many = {"candidates": [{"id": str(i), "url": "a.example/x"} for i in range(9)]}
    r = client.post("/v1/audition", json=many)
    assert r.status_code == 400 and r.json()["error"] == "too_many_candidates"
    assert client.post("/v1/audition", json={"candidates": []}).status_code == 400
    assert client.post("/v1/audition", json={"candidates": [{"id": "a"}]}).status_code == 400
    assert client.post("/v1/audition", content=b"nope").status_code == 400
    ok8 = {"candidates": [{"id": str(i), "url": "a.example/x"} for i in range(8)], "bioMs": 100}
    assert client.post("/v1/audition", json=ok8).status_code == 200


def test_audition_clamps_bio_ms(api):
    client, _ = api
    client.post("/v1/run/start")
    one = [{"id": "a", "url": "a.example/x"}]
    assert client.post("/v1/audition", json={"candidates": one, "bioMs": 5}).json()["bioMs"] == 100
    assert client.post("/v1/audition", json={"candidates": one, "bioMs": 99999}).json()["bioMs"] == 2000
    assert client.post("/v1/audition", json={"candidates": one}).json()["bioMs"] == 500


def test_listen_sets_and_clears_the_stimulus(api):
    client, _ = api
    client.post("/v1/run/start")
    r = client.post("/v1/listen", json={"url": "stream.example.com/loud.mp3"}).json()
    assert r["ok"] and r["error"] is None and isinstance(r["engagement"], float)
    assert set(r["populationRates"]) == set(OUTPUTS) and "loudness" in r["features"]
    state = client.get("/v1/state").json()
    assert state["listeningUrl"] == "stream.example.com/loud.mp3" and state["engagement"] == r["engagement"]
    assert state["lastFeatures"]["loudness"] == r["features"]["loudness"]
    bad = client.post("/v1/listen", json={"url": "stream.example.com/bad"}).json()
    assert bad["ok"] is False and bad["error"] == "unsupported_stream" and bad["engagement"] is None
    client.post("/v1/listen", json={"url": "stream.example.com/loud.mp3"})
    cleared = client.post("/v1/listen", json={"url": None}).json()
    assert cleared["ok"] and client.get("/v1/state").json()["listeningUrl"] is None
    assert client.post("/v1/listen", json={}).status_code == 400
    assert client.post("/v1/listen", json={"url": 5}).status_code == 400


# ------------------------------------------------------------- load modes


def read_frames(ws, seconds: float, limit: int = 200):
    frames = []
    deadline = time.time() + seconds
    while time.time() < deadline and len(frames) < limit:
        frames.append(decode_frame(ws.receive_bytes()))
    return frames


def test_stopped_mode_makes_zero_simulation_steps(api):
    client, engine = api
    time.sleep(0.2)  # let start-up bookkeeping settle
    steps = engine.total_steps
    with client.websocket_connect("/v1/stream") as ws:
        frames = read_frames(ws, 2.4)
        assert client.get("/v1/state").json()["viewers"] == 1
        assert client.get("/v1/state").json()["mode"] == "stopped"
    assert engine.total_steps == steps
    assert 2 <= len(frames) <= 5  # about one empty frame per second
    n_pop = len(client.get("/v1/model").json()["populations"])
    for f in frames:
        assert f["spikes"].size == 0 and f["rates"].size == n_pop and np.isnan(f["engagement"])


def test_bursting_mode_is_idle_between_requests(api):
    client, engine = api
    client.post("/v1/run/start")
    time.sleep(0.3)
    steps = engine.total_steps
    time.sleep(0.8)
    assert engine.total_steps == steps  # no viewers: nothing runs on its own
    client.post("/v1/audition", json={"candidates": [{"id": "a", "url": "a.example/x"}], "bioMs": 100})
    assert engine.total_steps > steps


def test_continuous_mode_streams_frames_and_returns_to_bursting(api):
    client, engine = api
    client.post("/v1/run/start")
    client.post("/v1/listen", json={"url": "stream.example.com/loud.mp3"})
    with client.websocket_connect("/v1/stream") as ws:
        frames = read_frames(ws, 2.0)
        state = client.get("/v1/state").json()
        assert state["mode"] == "continuous" and state["viewers"] == 1
        assert state["realtimeFactor"] > 0
        times = [f["bioMs"] for f in frames]
        assert times == sorted(times) and times[-1] > times[0]
        assert sum(f["spikes"].size for f in frames) > 0
        model = make_model()
        for f in frames:
            assert f["rates"].size == len(model.populations)
            assert (f["spikes"] < model.n_neurons).all() if f["spikes"].size else True
        assert any(np.isfinite(f["engagement"]) for f in frames)
        # the simulation is paced: far fewer than 'as fast as possible' frames in 2 s
        assert len(frames) < 80
    deadline = time.time() + 5
    while client.get("/v1/state").json()["viewers"] and time.time() < deadline:
        time.sleep(0.05)
    state = client.get("/v1/state").json()
    assert state["viewers"] == 0 and state["mode"] == "bursting"
    steps = engine.total_steps
    time.sleep(0.5)
    assert engine.total_steps == steps


def test_stop_halts_the_continuous_simulation(api):
    client, engine = api
    client.post("/v1/run/start")
    with client.websocket_connect("/v1/stream") as ws:
        read_frames(ws, 0.8)
        assert engine.total_steps > 0
        client.post("/v1/run/stop")
        steps = engine.total_steps
        frames = read_frames(ws, 1.6)
        assert engine.total_steps == steps
        assert frames and frames[-1]["spikes"].size == 0
        assert client.get("/v1/state").json()["mode"] == "stopped"


# ------------------------------------------------------------------- auth


def test_shared_secret_protects_http_and_websocket(fixture_model_path, tmp_path):
    client, _ = make_client(fixture_model_path, tmp_path, secret="s3cret")
    good = {"X-FlyDj-Token": "s3cret"}
    with client:
        wait_ready(client)
        for method, path in [("get", "/health"), ("get", "/v1/state"), ("get", "/v1/model"), ("get", "/v1/model/geometry"), ("post", "/v1/run/start"), ("post", "/v1/audition")]:
            r = getattr(client, method)(path)
            assert r.status_code == 401 and r.json()["error"] == "unauthorized", path
            wrong = getattr(client, method)(path, headers={"X-FlyDj-Token": "nope"})
            assert wrong.status_code == 401
        assert client.get("/v1/state", headers=good).status_code == 200
        assert client.post("/v1/run/start", headers=good).json()["running"]
        from starlette.testclient import WebSocketDenialResponse
        from starlette.websockets import WebSocketDisconnect

        with pytest.raises((WebSocketDenialResponse, WebSocketDisconnect)):
            with client.websocket_connect("/v1/stream"):
                pass
        with client.websocket_connect("/v1/stream", headers=good) as ws:
            assert decode_frame(ws.receive_bytes())["rates"].size > 0


def test_model_loading_failure_reports_error(tmp_path):
    settings = make_settings(tmp_path / "missing.npz", tmp_path)
    engine = Engine(settings)
    client = TestClient(create_app(settings, engine))
    with client:
        deadline = time.time() + 20
        while time.time() < deadline and client.get("/health").status_code == 200 and engine.status == "loading":
            time.sleep(0.05)
        r = client.get("/health")
        assert r.status_code == 503 and r.json()["modelStatus"] == "error"
        doc = client.get("/v1/model").json()
        assert doc["status"] == "error" and "artifact not found" in doc["message"] and "rebuild the image" in doc["message"]
        client.post("/v1/run/start")
        r = client.post("/v1/audition", json={"candidates": [{"id": "a", "url": "a.example/x"}]})
        assert r.status_code == 503


def test_fresh_audition_has_distinct_timestamped_evidence_and_validates_duration(api):
    client, _ = api
    client.post("/v1/run/start")
    request = {"candidates": [{"id": "a", "url": "a.example/loud"}, {"id": "b", "url": "a.example/bad"}], "sampleSeconds": 10, "fresh": True}
    first = client.post("/v1/audition", json=request).json()["results"]
    second = client.post("/v1/audition", json=request).json()["results"]
    for a, b in zip(first, second):
        assert a["sampleId"] and a["sampledAtUtc"]
        assert a["sampleId"] != b["sampleId"]
    for seconds in [0, 11, True, "ten", None]:
        assert client.post("/v1/audition", json=request | {"sampleSeconds": seconds}).status_code == 400
    assert client.post("/v1/audition", json=request | {"fresh": "yes"}).status_code == 400


def test_audio_fetching_is_limited_to_two_concurrent_streams(fixture_model_path, tmp_path):
    import threading
    barrier = threading.Barrier(2)
    lock = threading.Lock()
    active = 0
    peak = 0

    def sampler(url):
        nonlocal active, peak
        with lock:
            active += 1
            peak = max(peak, active)
        try:
            barrier.wait(timeout=5)
            return fake_features()
        finally:
            with lock:
                active -= 1

    client, _ = make_client(fixture_model_path, tmp_path, sampler=sampler)
    with client:
        wait_ready(client)
        client.post("/v1/run/start")
        response = client.post("/v1/audition", json={"candidates": [{"id": str(i), "url": f"radio{i}.example/live"} for i in range(8)], "fresh": True})
        assert response.status_code == 200
        assert all(result["ok"] for result in response.json()["results"])
        assert peak == 2
