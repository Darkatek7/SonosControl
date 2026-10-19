"""Fetching, decoding and analysing a few seconds of a radio stream.

Pipeline: ``normalize_url`` -> ``fetch_bytes`` (own HTTP client with address
checks, size and time limits, playlist resolution) -> ``decode_audio`` (PyAV)
-> ``analyze`` (band energies, loudness, onsets, centroid) ->
``rates_from_features`` (Poisson rates for the Johnston's organ populations).

Mapping of audio to neural input (deterministic, all rates in Hz, 0..200):

``jo_a``   (high-frequency sound)  ``200 * loudness * (high + 0.5 * mid)``
``jo_b``   (low-frequency, beat)   ``200 * loudness * low * (0.4 + 0.6 * onset)``
``jo_ce``  (slow deflection)       ``200 * (0.5 * loudness + 0.5 * onset)``

where ``onset = min(onsetRate / 6, 1)``. Band shares sum to 1; ``loudness`` is
the RMS level mapped from -36..-8 dBFS onto 0..1.
"""

from __future__ import annotations

import http.client
import io
import ipaddress
import socket
import ssl
import threading
import time
import urllib.parse
from dataclasses import dataclass

import numpy as np

SAMPLE_RATE = 22050
MAX_DECODE_SECONDS = 4.0
MAX_BYTES = 2 * 1024 * 1024
MAX_PLAYLIST_BYTES = 64 * 1024
CONNECT_TIMEOUT = 5.0
TOTAL_TIMEOUT = 8.0
READ_BUDGET = 5.0
MAX_REDIRECTS = 4
CACHE_TTL_SECONDS = 120.0
MIN_AUDIO_SECONDS = 0.8
MAX_RATE_HZ = 200.0
LOUDNESS_FLOOR_DBFS = 36.0  # RMS at -36 dBFS maps to 0 ...
LOUDNESS_CEIL_DBFS = 8.0  # ... and -8 dBFS to 1 (broadcast music sits around -20..-10)
USER_AGENT = "FlyDJ/0.1 (+SonosControl)"

LOW_EDGE_HZ = 250.0
HIGH_EDGE_HZ = 2000.0
N_FFT = 1024
HOP = 512

PLAYLIST_TYPES = {
    "audio/x-mpegurl",
    "audio/mpegurl",
    "audio/x-scpls",
    "application/pls+xml",
    "audio/scpls",
}
HLS_TYPES = {"application/vnd.apple.mpegurl", "application/x-mpegurl"}


class AudioError(Exception):
    """A stream could not be used; ``code`` is the machine-readable reason."""

    def __init__(self, code: str, message: str | None = None) -> None:
        super().__init__(message or code)
        self.code = code
        self.message = message or code


@dataclass(frozen=True)
class Features:
    low: float
    mid: float
    high: float
    loudness: float
    onset_rate: float
    centroid_hz: float
    envelope: tuple[float, ...] = ()

    @property
    def onset(self) -> float:
        return min(self.onset_rate / 6.0, 1.0)

    def to_api(self) -> dict:
        return {
            "low": round(self.low, 4),
            "mid": round(self.mid, 4),
            "high": round(self.high, 4),
            "loudness": round(self.loudness, 4),
            "onsetRate": round(self.onset_rate, 3),
            "centroidHz": round(self.centroid_hz, 1),
        }


# ---------------------------------------------------------------------------
# URL handling and network safety
# ---------------------------------------------------------------------------


def normalize_url(url: str) -> str:
    """Return a canonical http(s) URL; add ``http://`` when no scheme is given."""
    text = (url or "").strip()
    if not text or len(text) > 2048 or any(ch in text for ch in "\r\n\t "):
        raise AudioError("invalid_url", "The stream address is empty or malformed.")
    if "://" not in text:
        text = "http://" + text.lstrip("/")
    parts = urllib.parse.urlsplit(text)
    if parts.scheme.lower() not in ("http", "https"):
        raise AudioError("invalid_url", "Only http and https streams are supported.")
    if not parts.hostname:
        raise AudioError("invalid_url", "The stream address has no host.")
    try:
        parts.port  # noqa: B018 - validates the port
    except ValueError as exc:
        raise AudioError("invalid_url", "The stream address has an invalid port.") from exc
    path = parts.path or "/"
    netloc = parts.netloc.lower()
    return urllib.parse.urlunsplit((parts.scheme.lower(), netloc, path, parts.query, ""))


_BLOCKED_NETWORKS = [
    ipaddress.ip_network("169.254.0.0/16"),  # link-local, cloud metadata
    ipaddress.ip_network("0.0.0.0/8"),
    ipaddress.ip_network("100.100.100.200/32"),  # Alibaba Cloud metadata
    ipaddress.ip_network("fe80::/10"),
    ipaddress.ip_network("fd00:ec2::254/128"),  # AWS IPv6 metadata
]


def _is_blocked(ip: ipaddress._BaseAddress, allow_loopback: bool) -> bool:
    if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped is not None:
        ip = ip.ipv4_mapped
    if ip.is_loopback:
        return not allow_loopback
    if ip.is_link_local or ip.is_multicast or ip.is_unspecified or ip.is_reserved:
        return True
    return any(ip in net for net in _BLOCKED_NETWORKS if net.version == ip.version)


def resolve_safe(host: str, port: int, allow_loopback: bool = False) -> str:
    """Resolve ``host`` and return one address, refusing blocked ranges."""
    try:
        infos = socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)
    except socket.gaierror as exc:
        raise AudioError("fetch_failed", "The stream host could not be resolved.") from exc
    addresses = []
    for info in infos:
        ip = ipaddress.ip_address(info[4][0].split("%")[0])
        if _is_blocked(ip, allow_loopback):
            raise AudioError("blocked_address", "The stream address points to a blocked network range.")
        addresses.append(str(ip))
    if not addresses:
        raise AudioError("fetch_failed", "The stream host has no address.")
    return addresses[0]


class _IcyResponse(http.client.HTTPResponse):
    """Accept the non-standard ``ICY 200 OK`` status line of Shoutcast servers."""

    def _read_status(self):  # type: ignore[override]
        line = self.fp.readline(65537)
        if line.startswith(b"ICY "):
            line = b"HTTP/1.0 " + line[4:]
        text = str(line, "iso-8859-1")
        try:
            version, status, reason = (text.split(None, 2) + [""])[:3]
            return version, int(status), reason.strip()
        except ValueError as exc:
            raise http.client.BadStatusLine(text) from exc


class _PinnedHTTP(http.client.HTTPConnection):
    response_class = _IcyResponse

    def __init__(self, host: str, port: int, ip: str, timeout: float) -> None:
        super().__init__(host, port, timeout=timeout)
        self._ip = ip

    def connect(self) -> None:
        self.sock = socket.create_connection((self._ip, self.port), self.timeout)


def _tls_context() -> ssl.SSLContext:
    """Verifying client context; X.509 strict mode is relaxed because many
    radio hosts present chains that lack optional extensions (for example the
    Authority Key Identifier) and are otherwise valid."""
    ctx = ssl.create_default_context()
    ctx.verify_flags &= ~ssl.VERIFY_X509_STRICT
    return ctx


class _PinnedHTTPS(http.client.HTTPSConnection):
    response_class = _IcyResponse

    def __init__(self, host: str, port: int, ip: str, timeout: float) -> None:
        super().__init__(host, port, timeout=timeout)
        self._ip = ip
        self._ctx = _tls_context()

    def connect(self) -> None:
        raw = socket.create_connection((self._ip, self.port), self.timeout)
        self.sock = self._ctx.wrap_socket(raw, server_hostname=self.host)


def _open(url: str, deadline: float, allow_loopback: bool):
    parts = urllib.parse.urlsplit(url)
    host = parts.hostname or ""
    port = parts.port or (443 if parts.scheme == "https" else 80)
    ip = resolve_safe(host, port, allow_loopback)
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise AudioError("timeout", "The stream took too long to respond.")
    cls = _PinnedHTTPS if parts.scheme == "https" else _PinnedHTTP
    conn = cls(host, port, ip, min(CONNECT_TIMEOUT, remaining))
    target = parts.path or "/"
    if parts.query:
        target += "?" + parts.query
    try:
        conn.request(
            "GET",
            target,
            headers={
                "Host": parts.netloc,
                "User-Agent": USER_AGENT,
                "Accept": "*/*",
                "Icy-MetaData": "0",
                "Connection": "close",
            },
        )
        response = conn.getresponse()
    except socket.timeout as exc:
        conn.close()
        raise AudioError("timeout", "The stream took too long to respond.") from exc
    except (OSError, http.client.HTTPException, ssl.SSLError) as exc:
        conn.close()
        raise AudioError("fetch_failed", "The stream could not be reached.") from exc
    return conn, response


def _read_limited(conn, response, limit: int, deadline: float, target: int | None = None) -> bytes:
    """Read up to ``limit`` bytes, stopping at ``target`` bytes or the time budget."""
    started = time.monotonic()
    stop_at = min(deadline, started + READ_BUDGET)
    chunks: list[bytes] = []
    total = 0
    while total < limit:
        now = time.monotonic()
        if now >= stop_at:
            break
        if target is not None and total >= target:
            break
        try:
            if conn.sock is not None:
                conn.sock.settimeout(max(0.2, min(3.0, stop_at - now)))
            block = response.read(min(16384, limit - total))
        except socket.timeout:
            break
        except (OSError, http.client.HTTPException):
            break
        if not block:
            break
        chunks.append(block)
        total += len(block)
    return b"".join(chunks)


def parse_playlist(text: str, base_url: str) -> str | None:
    """First http(s) entry of a ``.pls`` or ``.m3u`` playlist (or None)."""
    lines = [raw.strip() for raw in text.splitlines()]
    is_pls = any(line.lower().startswith("[playlist]") for line in lines)
    for line in lines:
        if not line or line.startswith("#"):
            continue
        if is_pls:
            key, _, value = line.partition("=")
            if not (key.lower().startswith("file") and value.strip()):
                continue
            line = value.strip()
        absolute = urllib.parse.urljoin(base_url, line)
        if absolute.lower().startswith(("http://", "https://")):
            return absolute
    return None


def is_hls_text(text: str) -> bool:
    return "#EXT-X-" in text or "#EXT-X-STREAM-INF" in text


def fetch_bytes(url: str, allow_loopback: bool = False, _depth: int = 0) -> bytes:
    """Download the leading bytes of the stream behind ``url``.

    Follows redirects (validating every hop) and resolves ``.m3u``/``.pls``
    playlists. HLS is rejected as ``unsupported_stream``.
    """
    if _depth > 2:
        raise AudioError("fetch_failed", "Too many nested playlists.")
    deadline = time.monotonic() + TOTAL_TIMEOUT
    current = normalize_url(url)
    for _ in range(MAX_REDIRECTS + 1):
        conn, response = _open(current, deadline, allow_loopback)
        try:
            status = response.status
            if status in (301, 302, 303, 307, 308):
                location = response.getheader("Location")
                if not location:
                    raise AudioError("http_error", "The stream redirected without a target.")
                current = normalize_url(urllib.parse.urljoin(current, location))
                continue
            if status not in (200, 206):
                raise AudioError("http_error", f"The stream answered with HTTP {status}.")
            ctype = (response.getheader("Content-Type") or "").split(";")[0].strip().lower()
            path = urllib.parse.urlsplit(current).path.lower()
            if path.endswith(".m3u8") or ctype in HLS_TYPES:
                raise AudioError("unsupported_stream", "HLS streams are not supported.")
            if ctype in ("text/html", "application/xhtml+xml", "application/json"):
                raise AudioError("unsupported_stream", "The address does not point to an audio stream.")
            is_playlist = path.endswith((".m3u", ".pls")) or ctype in PLAYLIST_TYPES
            if is_playlist:
                body = _read_limited(conn, response, MAX_PLAYLIST_BYTES, deadline)
                text = body.decode("utf-8", errors="replace")
                if is_hls_text(text):
                    raise AudioError("unsupported_stream", "HLS streams are not supported.")
                entry = parse_playlist(text, current)
                if entry is None:
                    raise AudioError("unsupported_stream", "The playlist has no http(s) stream entry.")
                return fetch_bytes(entry, allow_loopback, _depth + 1)
            target = None
            bitrate = response.getheader("icy-br")
            if bitrate and bitrate.split(",")[0].strip().isdigit():
                kbps = min(max(int(bitrate.split(",")[0]), 16), 512)
                target = int(kbps * 125 * (MAX_DECODE_SECONDS + 0.4))
            else:
                target = 96 * 1024
            data = _read_limited(conn, response, MAX_BYTES, deadline, target)
        finally:
            conn.close()
        if len(data) < 2048:
            raise AudioError("audio_too_short", "The stream delivered almost no data.")
        return data
    raise AudioError("fetch_failed", "Too many redirects.")


# ---------------------------------------------------------------------------
# Decoding and analysis
# ---------------------------------------------------------------------------


def decode_audio(data: bytes) -> np.ndarray:
    """Decode to mono float32 at ``SAMPLE_RATE`` (at most ``MAX_DECODE_SECONDS``)."""
    import av

    max_samples = int(MAX_DECODE_SECONDS * SAMPLE_RATE)
    parts: list[np.ndarray] = []
    got = 0
    try:
        with av.open(io.BytesIO(data), mode="r") as container:
            stream = next((s for s in container.streams if s.type == "audio"), None)
            if stream is None:
                raise AudioError("unsupported_stream", "The stream contains no audio.")
            resampler = av.AudioResampler(format="flt", layout="mono", rate=SAMPLE_RATE)
            try:
                for frame in container.decode(stream):
                    for out in resampler.resample(frame):
                        arr = out.to_ndarray().reshape(-1).astype(np.float32, copy=False)
                        parts.append(arr)
                        got += arr.shape[0]
                    if got >= max_samples:
                        break
            except av.error.FFmpegError:
                if got == 0:
                    raise
                # a truncated tail of a live stream is expected
    except AudioError:
        raise
    except Exception as exc:  # noqa: BLE001 - PyAV raises many error types
        raise AudioError("decode_failed", "The stream could not be decoded.") from exc
    if not parts:
        raise AudioError("decode_failed", "The stream produced no audio.")
    return np.concatenate(parts)[:max_samples]


def analyze(samples: np.ndarray, sample_rate: int = SAMPLE_RATE) -> Features:
    """Band shares, loudness, onset rate and spectral centroid of ``samples``."""
    x = np.asarray(samples, dtype=np.float32).reshape(-1)
    x = x[: int(MAX_DECODE_SECONDS * sample_rate)]
    duration = x.shape[0] / sample_rate
    if duration < MIN_AUDIO_SECONDS:
        raise AudioError("audio_too_short", "Less than a second of audio could be decoded.")
    x = np.nan_to_num(x, nan=0.0, posinf=0.0, neginf=0.0)
    rms = float(np.sqrt(np.mean(x.astype(np.float64) ** 2)))
    dbfs = 20.0 * np.log10(max(rms, 1e-9))
    loudness = float(np.clip((dbfs + LOUDNESS_FLOOR_DBFS) / (LOUDNESS_FLOOR_DBFS - LOUDNESS_CEIL_DBFS), 0.0, 1.0))

    n_frames = 1 + (x.shape[0] - N_FFT) // HOP
    index = np.arange(N_FFT)[None, :] + HOP * np.arange(n_frames)[:, None]
    frames = x[index] * np.hanning(N_FFT).astype(np.float32)[None, :]
    mag = np.abs(np.fft.rfft(frames, axis=1)).astype(np.float64) / (N_FFT / 4.0)
    power = mag**2
    freqs = np.fft.rfftfreq(N_FFT, 1.0 / sample_rate)
    low_mask = freqs < LOW_EDGE_HZ
    mid_mask = (freqs >= LOW_EDGE_HZ) & (freqs < HIGH_EDGE_HZ)
    high_mask = freqs >= HIGH_EDGE_HZ
    low_p = float(power[:, low_mask].sum())
    mid_p = float(power[:, mid_mask].sum())
    high_p = float(power[:, high_mask].sum())
    total = low_p + mid_p + high_p
    if total < 1e-12:
        low = mid = high = 1.0 / 3.0
        centroid = 0.0
    else:
        low, mid, high = low_p / total, mid_p / total, high_p / total
        spectrum = power.sum(axis=0)
        centroid = float((freqs * spectrum).sum() / spectrum.sum())

    # onsets: half-wave rectified flux of the log-compressed spectrogram
    logmag = np.log1p(100.0 * mag)
    flux = np.maximum(logmag[1:] - logmag[:-1], 0.0).sum(axis=1)
    onsets = _count_onsets(flux, sample_rate / HOP)
    onset_rate = onsets / duration

    window = int(0.25 * sample_rate)
    n_windows = max(1, x.shape[0] // window)
    env = np.array([np.sqrt(np.mean(x[i * window : (i + 1) * window].astype(np.float64) ** 2)) for i in range(n_windows)])
    envelope = tuple(float(v) for v in (env / env.mean() if env.mean() > 1e-9 else np.ones_like(env)))
    return Features(low, mid, high, loudness, float(onset_rate), centroid, envelope)


def _count_onsets(flux: np.ndarray, frame_rate: float) -> int:
    """Peak-pick the spectral flux; steady tones and noise give (almost) none."""
    if flux.shape[0] < 3:
        return 0
    peak = float(flux.max())
    if peak < 5.0:  # absolute floor in log-magnitude units
        return 0
    median = float(np.median(flux))
    mad = float(np.median(np.abs(flux - median))) * 1.4826
    threshold = max(median + 3.0 * mad, 0.35 * peak, 5.0)
    min_gap = max(1, int(0.08 * frame_rate))
    count = 0
    last = -min_gap
    for i in range(1, flux.shape[0] - 1):
        if flux[i] >= threshold and flux[i] >= flux[i - 1] and flux[i] > flux[i + 1] and i - last >= min_gap:
            count += 1
            last = i
    return count


def rates_from_features(f: Features) -> dict[str, float]:
    """Poisson rates (Hz) for the Johnston's organ input populations."""
    jo_a = MAX_RATE_HZ * f.loudness * (f.high + 0.5 * f.mid)
    jo_b = MAX_RATE_HZ * f.loudness * f.low * (0.4 + 0.6 * f.onset)
    jo_ce = MAX_RATE_HZ * (0.5 * f.loudness + 0.5 * f.onset)
    return {
        "jo_a": float(np.clip(jo_a, 0.0, MAX_RATE_HZ)),
        "jo_b": float(np.clip(jo_b, 0.0, MAX_RATE_HZ)),
        "jo_ce": float(np.clip(jo_ce, 0.0, MAX_RATE_HZ)),
    }


# ---------------------------------------------------------------------------
# Cache
# ---------------------------------------------------------------------------


class FeatureCache:
    """Per-URL feature cache with a time-to-live."""

    def __init__(self, ttl: float = CACHE_TTL_SECONDS, clock=time.monotonic) -> None:
        self._ttl = ttl
        self._clock = clock
        self._items: dict[str, tuple[float, Features]] = {}
        self._lock = threading.Lock()

    def get(self, key: str) -> Features | None:
        with self._lock:
            item = self._items.get(key)
            if item is None:
                return None
            stamp, features = item
            if self._clock() - stamp > self._ttl:
                del self._items[key]
                return None
            return features

    def put(self, key: str, features: Features) -> None:
        with self._lock:
            now = self._clock()
            self._items[key] = (now, features)
            if len(self._items) > 64:
                for stale in [k for k, (t, _) in self._items.items() if now - t > self._ttl]:
                    del self._items[stale]


def sample_stream(url: str, cache: FeatureCache | None = None, allow_loopback: bool = False) -> Features:
    """Fetch, decode and analyse ``url`` (cached for 120 s)."""
    key = normalize_url(url)
    if cache is not None:
        hit = cache.get(key)
        if hit is not None:
            return hit
    features = analyze(decode_audio(fetch_bytes(key, allow_loopback)))
    if cache is not None:
        cache.put(key, features)
    return features
