import io
import socket
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
import pytest

from app import audio
from app.audio import SAMPLE_RATE, AudioError, Features, FeatureCache, analyze, rates_from_features

SR = SAMPLE_RATE


def tone(freq: float, seconds: float = 3.0, amp: float = 0.5) -> np.ndarray:
    t = np.arange(int(seconds * SR)) / SR
    return (amp * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def pulse_train(rate_hz: float, seconds: float = 4.0, amp: float = 0.6) -> np.ndarray:
    x = np.zeros(int(seconds * SR), dtype=np.float32)
    burst = int(0.03 * SR)
    t = np.arange(burst) / SR
    env = np.hanning(burst)
    for k in range(int(seconds * rate_hz)):
        start = int((k + 0.5) / rate_hz * SR)
        if start + burst < x.shape[0]:
            x[start : start + burst] = amp * env * np.sin(2 * np.pi * 1000 * t)
    return x


def wav_bytes(samples: np.ndarray) -> bytes:
    buf = io.BytesIO()
    with wave.open(buf, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(SR)
        handle.writeframes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())
    return buf.getvalue()


# --------------------------------------------------------------------- analysis


@pytest.mark.parametrize(
    "freq,band", [(100, "low"), (1000, "mid"), (5000, "high")]
)
def test_band_shares_follow_the_dominant_frequency(freq, band):
    f = analyze(tone(freq))
    assert f.low + f.mid + f.high == pytest.approx(1.0)
    assert getattr(f, band) > 0.95
    assert f.centroid_hz == pytest.approx(freq, rel=0.1)


def test_loudness_maps_rms_dbfs():
    assert analyze(tone(440, amp=0.5)).loudness > 0.9  # about -9 dBFS RMS
    assert 0.15 < analyze(tone(440, amp=0.05)).loudness < 0.4  # about -29 dBFS
    assert analyze(tone(440, amp=0.0005)).loudness == 0.0


def test_silence_has_neutral_bands_and_zero_loudness():
    f = analyze(np.zeros(3 * SR, dtype=np.float32))
    assert f.loudness == 0.0
    assert f.low == pytest.approx(1 / 3) and f.onset_rate == 0.0


def test_onset_rate_counts_pulses_not_steady_tones():
    assert analyze(pulse_train(4.0)).onset_rate == pytest.approx(4.0, abs=1.0)
    assert analyze(pulse_train(2.0)).onset_rate == pytest.approx(2.0, abs=0.75)
    assert analyze(tone(800)).onset_rate == 0.0
    rng = np.random.default_rng(1)
    noise = (0.1 * rng.standard_normal(3 * SR)).astype(np.float32)
    assert analyze(noise).onset_rate < 1.5


def test_too_short_audio_is_rejected():
    with pytest.raises(AudioError) as err:
        analyze(tone(440, seconds=0.3))
    assert err.value.code == "audio_too_short"


def test_analysis_is_deterministic():
    x = pulse_train(3.0) + tone(300, 4.0, 0.1)
    assert analyze(x) == analyze(x.copy())


# ---------------------------------------------------------------------- mapping


def feats(**kw):
    base = dict(low=0.3, mid=0.4, high=0.3, loudness=0.7, onset_rate=3.0, centroid_hz=1200.0)
    base.update(kw)
    return Features(**base)


def test_rates_are_bounded_and_documented_formula():
    r = rates_from_features(feats())
    assert r["jo_a"] == pytest.approx(200 * 0.7 * (0.3 + 0.5 * 0.4))
    assert r["jo_b"] == pytest.approx(200 * 0.7 * 0.3 * (0.4 + 0.6 * 0.5))
    assert r["jo_ce"] == pytest.approx(200 * (0.5 * 0.7 + 0.5 * 0.5))
    for f in (feats(loudness=1.0, high=1.0, low=0.0, mid=0.0, onset_rate=60), feats(loudness=0.0, onset_rate=0)):
        assert all(0.0 <= v <= 200.0 for v in rates_from_features(f).values())
    assert rates_from_features(feats(loudness=0.0, onset_rate=0.0)) == {"jo_a": 0.0, "jo_b": 0.0, "jo_ce": 0.0}


def test_rates_follow_the_spectrum():
    bright = rates_from_features(feats(low=0.05, mid=0.2, high=0.75))
    bassy = rates_from_features(feats(low=0.8, mid=0.15, high=0.05))
    assert bright["jo_a"] > bassy["jo_a"] and bassy["jo_b"] > bright["jo_b"]
    assert rates_from_features(feats(loudness=0.9))["jo_ce"] > rates_from_features(feats(loudness=0.2))["jo_ce"]


# ----------------------------------------------------------------- url + safety


def test_normalize_url():
    assert audio.normalize_url("stream.example.com/live.mp3") == "http://stream.example.com/live.mp3"
    assert audio.normalize_url(" HTTPS://Example.COM:8000/x?y=1 ") == "https://example.com:8000/x?y=1"
    assert audio.normalize_url("http://host") == "http://host/"
    for bad in ["", "   ", "ftp://example.com/x", "file:///etc/passwd", "http://", "a b", "http://h:99999/x"]:
        with pytest.raises(AudioError) as err:
            audio.normalize_url(bad)
        assert err.value.code == "invalid_url"


def test_address_blocking():
    ip = __import__("ipaddress").ip_address
    assert audio._is_blocked(ip("169.254.169.254"), False)
    assert audio._is_blocked(ip("::ffff:169.254.169.254"), False)
    assert audio._is_blocked(ip("fe80::1"), False)
    assert audio._is_blocked(ip("0.0.0.0"), False)
    assert audio._is_blocked(ip("127.0.0.1"), False) and not audio._is_blocked(ip("127.0.0.1"), True)
    assert not audio._is_blocked(ip("192.168.1.20"), False)  # LAN radio is fine
    assert not audio._is_blocked(ip("93.184.216.34"), False)


def test_metadata_endpoint_is_refused_before_connecting():
    with pytest.raises(AudioError) as err:
        audio.fetch_bytes("http://169.254.169.254/latest/meta-data/")
    assert err.value.code == "blocked_address"


def test_parse_playlist():
    pls = "[playlist]\nNumberOfEntries=2\nFile1=http://a.example/one\nTitle1=One\nFile2=http://b.example/two\n"
    assert audio.parse_playlist(pls, "http://x/y.pls") == "http://a.example/one"
    m3u = "#EXTM3U\n#EXTINF:-1,Name\nhttp://c.example/live\n"
    assert audio.parse_playlist(m3u, "http://x/y.m3u") == "http://c.example/live"
    assert audio.parse_playlist("#EXTM3U\nlive.mp3\n", "http://x/dir/y.m3u") == "http://x/dir/live.mp3"
    assert audio.parse_playlist("# nothing\n", "http://x/y.m3u") is None


def test_feature_cache_expires():
    now = [0.0]
    cache = FeatureCache(ttl=120.0, clock=lambda: now[0])
    f = feats()
    cache.put("k", f)
    now[0] = 119.0
    assert cache.get("k") == f
    now[0] = 121.0
    assert cache.get("k") is None


# ------------------------------------------------------------- decode + network


def test_decode_resamples_to_mono_22050():
    x = tone(440, 1.5)
    out = audio.decode_audio(wav_bytes(x))
    assert out.dtype == np.float32 and abs(out.shape[0] - x.shape[0]) < 50
    assert analyze(out).centroid_hz == pytest.approx(440, rel=0.1)


def test_decode_limits_to_four_seconds():
    out = audio.decode_audio(wav_bytes(tone(300, 9.0)))
    assert out.shape[0] <= int(4.0 * SR)


def test_decode_rejects_garbage():
    with pytest.raises(AudioError) as err:
        audio.decode_audio(b"this is not audio" * 400)
    assert err.value.code in ("decode_failed", "unsupported_stream")


class _Handler(BaseHTTPRequestHandler):
    wav = wav_bytes(tone(500, 4.0, 0.4))

    def log_message(self, *args):  # silence
        pass

    def do_GET(self):  # noqa: N802
        path = self.path
        if path == "/stream.wav":
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.end_headers()
            self.wfile.write(self.wav)
        elif path == "/list.pls":
            host = self.headers["Host"]
            body = f"[playlist]\nFile1=http://{host}/stream.wav\n".encode()
            self.send_response(200)
            self.send_header("Content-Type", "audio/x-scpls")
            self.end_headers()
            self.wfile.write(body)
        elif path == "/hls.m3u8":
            self.send_response(200)
            self.send_header("Content-Type", "application/vnd.apple.mpegurl")
            self.end_headers()
            self.wfile.write(b"#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:5,\nseg0.ts\n")
        elif path == "/page":
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            self.wfile.write(b"<html></html>")
        elif path == "/redirect-meta":
            self.send_response(302)
            self.send_header("Location", "http://169.254.169.254/latest/meta-data/")
            self.end_headers()
        elif path == "/redirect-ok":
            self.send_response(302)
            self.send_header("Location", "/stream.wav")
            self.end_headers()
        else:
            self.send_response(404)
            self.end_headers()


@pytest.fixture(scope="module")
def http_server():
    server = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    yield f"127.0.0.1:{server.server_address[1]}"
    server.shutdown()


def test_loopback_is_blocked_by_default(http_server):
    with pytest.raises(AudioError) as err:
        audio.fetch_bytes(f"http://{http_server}/stream.wav")
    assert err.value.code == "blocked_address"


def test_fetch_decode_analyze_over_http(http_server):
    f = audio.analyze(audio.decode_audio(audio.fetch_bytes(f"{http_server}/stream.wav", allow_loopback=True)))
    assert f.mid > 0.9 and f.loudness > 0.6


def test_playlist_and_redirect_are_followed(http_server):
    for path in ("/list.pls", "/redirect-ok"):
        data = audio.fetch_bytes(f"http://{http_server}{path}", allow_loopback=True)
        assert len(data) > 2048


@pytest.mark.parametrize(
    "path,code",
    [
        ("/hls.m3u8", "unsupported_stream"),
        ("/page", "unsupported_stream"),
        ("/redirect-meta", "blocked_address"),
        ("/missing", "http_error"),
    ],
)
def test_fetch_errors(http_server, path, code):
    with pytest.raises(AudioError) as err:
        audio.fetch_bytes(f"http://{http_server}{path}", allow_loopback=True)
    assert err.value.code == code


def test_shoutcast_icy_status_line_is_accepted():
    wav = wav_bytes(tone(700, 3.0, 0.4))
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]

    def serve():
        conn, _ = listener.accept()
        conn.recv(4096)
        conn.sendall(b"ICY 200 OK\r\nicy-name: Test\r\nicy-br: 128\r\ncontent-type: audio/wav\r\n\r\n" + wav)
        conn.close()
        listener.close()

    threading.Thread(target=serve, daemon=True).start()
    data = audio.fetch_bytes(f"http://127.0.0.1:{port}/", allow_loopback=True)
    assert len(data) > 2048


def test_sample_stream_uses_the_cache(http_server):
    cache = FeatureCache()
    url = f"http://{http_server}/stream.wav"
    first = audio.sample_stream(url, cache, allow_loopback=True)
    # a second call must not hit the network (loopback is blocked now)
    assert audio.sample_stream(url, cache, allow_loopback=False) == first
