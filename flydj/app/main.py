"""FastAPI application implementing the contract in ``API.md``."""

from __future__ import annotations

import asyncio
import hmac
import json
import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request, Response, WebSocket
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException
from starlette.websockets import WebSocketDisconnect

from .config import Settings
from .engine import MAX_CANDIDATES, DEFAULT_BIO_MS, ApiError, Engine

log = logging.getLogger("flydj")

TOKEN_HEADER = b"x-flydj-token"


def _error(status: int, code: str, message: str) -> JSONResponse:
    return JSONResponse({"error": code, "message": message}, status_code=status)


class SharedSecretMiddleware:
    """Require ``X-FlyDj-Token`` on every HTTP request and WebSocket upgrade."""

    def __init__(self, app, secret: str) -> None:
        self.app = app
        self.secret = secret.encode("utf-8")

    async def __call__(self, scope, receive, send):
        if not self.secret or scope["type"] not in ("http", "websocket"):
            await self.app(scope, receive, send)
            return
        token = b""
        for name, value in scope.get("headers", []):
            if name == TOKEN_HEADER:
                token = value
                break
        if hmac.compare_digest(token, self.secret):
            await self.app(scope, receive, send)
            return
        body = json.dumps({"error": "unauthorized", "message": "Missing or invalid X-FlyDj-Token."}).encode()
        if scope["type"] == "http":
            await send(
                {
                    "type": "http.response.start",
                    "status": 401,
                    "headers": [(b"content-type", b"application/json"), (b"content-length", str(len(body)).encode())],
                }
            )
            await send({"type": "http.response.body", "body": body})
            return
        await receive()  # websocket.connect
        if "websocket.http.response" in scope.get("extensions", {}):
            await send(
                {
                    "type": "websocket.http.response.start",
                    "status": 401,
                    "headers": [(b"content-type", b"application/json"), (b"content-length", str(len(body)).encode())],
                }
            )
            await send({"type": "websocket.http.response.body", "body": body})
        else:
            await send({"type": "websocket.close", "code": 1008})


async def _json_body(request: Request) -> dict:
    try:
        body = await request.json()
    except (json.JSONDecodeError, UnicodeDecodeError, ValueError):
        raise ApiError(400, "invalid_request", "The request body must be JSON.") from None
    if not isinstance(body, dict):
        raise ApiError(400, "invalid_request", "The request body must be a JSON object.")
    return body


def create_app(settings: Settings | None = None, engine: Engine | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    engine = engine or Engine(settings)

    @asynccontextmanager
    async def lifespan(_app: FastAPI):
        await engine.start()
        try:
            yield
        finally:
            await engine.shutdown()

    app = FastAPI(
        title="FlyDJ sidecar",
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
        lifespan=lifespan,
    )
    app.state.engine = engine
    app.add_middleware(SharedSecretMiddleware, secret=settings.shared_secret)

    @app.exception_handler(ApiError)
    async def _api_error(_request: Request, exc: ApiError):
        return _error(exc.status, exc.code, exc.message)

    @app.exception_handler(StarletteHTTPException)
    async def _http_error(_request: Request, exc: StarletteHTTPException):
        codes = {404: "not_found", 405: "method_not_allowed"}
        return _error(exc.status_code, codes.get(exc.status_code, "http_error"), str(exc.detail))

    @app.exception_handler(Exception)
    async def _unhandled(_request: Request, exc: Exception):
        log.exception("unhandled error", exc_info=exc)
        return _error(500, "internal_error", "Unexpected server error.")

    @app.get("/health")
    async def health():
        status = engine.status
        body = {"status": "error" if status == "error" else "ok", "modelStatus": status}
        return JSONResponse(body, status_code=503 if status == "error" else 200)

    @app.get("/v1/model")
    async def model():
        return JSONResponse(engine.model_doc())

    @app.get("/v1/model/geometry")
    async def geometry(request: Request):
        data, etag = engine.geometry()
        candidates = [c.strip().removeprefix("W/") for c in request.headers.get("if-none-match", "").split(",")]
        headers = {"ETag": etag, "Cache-Control": "private, max-age=0, must-revalidate"}
        if etag in candidates or "*" in candidates:
            return Response(status_code=304, headers=headers)
        return Response(data, media_type="application/octet-stream", headers=headers)

    @app.get("/v1/state")
    async def state():
        return JSONResponse(engine.state())

    @app.post("/v1/run/start")
    async def run_start():
        return JSONResponse(await engine.run_start())

    @app.post("/v1/run/stop")
    async def run_stop():
        return JSONResponse(await engine.run_stop())

    @app.post("/v1/audition")
    async def audition(request: Request):
        body = await _json_body(request)
        raw = body.get("candidates")
        if not isinstance(raw, list) or not raw:
            raise ApiError(400, "invalid_request", "candidates must be a non-empty array.")
        if len(raw) > MAX_CANDIDATES:
            raise ApiError(400, "too_many_candidates", f"At most {MAX_CANDIDATES} candidates are allowed.")
        candidates: list[tuple[str, str]] = []
        for item in raw:
            if not isinstance(item, dict) or not isinstance(item.get("url"), str):
                raise ApiError(400, "invalid_request", "Each candidate needs a string id and url.")
            cid = item.get("id")
            if not isinstance(cid, (str, int)) or isinstance(cid, bool):
                raise ApiError(400, "invalid_request", "Each candidate needs a string id and url.")
            candidates.append((str(cid), item["url"]))
        bio_ms = body.get("bioMs", DEFAULT_BIO_MS)
        if bio_ms is None:
            bio_ms = DEFAULT_BIO_MS
        if not isinstance(bio_ms, (int, float)) or isinstance(bio_ms, bool) or bio_ms != bio_ms:
            raise ApiError(400, "invalid_request", "bioMs must be a number.")
        return JSONResponse(await engine.audition(candidates, float(bio_ms)))

    @app.post("/v1/listen")
    async def listen(request: Request):
        body = await _json_body(request)
        if "url" not in body or not (body["url"] is None or isinstance(body["url"], str)):
            raise ApiError(400, "invalid_request", "url must be a string or null.")
        return JSONResponse(await engine.listen(body["url"]))

    @app.websocket("/v1/stream")
    async def stream(ws: WebSocket):
        await ws.accept()
        viewer = engine.hub.add()
        receive = asyncio.ensure_future(ws.receive())
        fetch = asyncio.ensure_future(viewer.queue.get())
        try:
            if engine.mode != "continuous":
                viewer.push(engine.empty_frame())
            while True:
                done, _ = await asyncio.wait({receive, fetch}, return_when=asyncio.FIRST_COMPLETED)
                if fetch in done:
                    await ws.send_bytes(fetch.result())
                    fetch = asyncio.ensure_future(viewer.queue.get())
                if receive in done:
                    message = receive.result()
                    if message["type"] == "websocket.disconnect":
                        break
                    receive = asyncio.ensure_future(ws.receive())
        except (WebSocketDisconnect, RuntimeError):
            pass
        finally:
            receive.cancel()
            fetch.cancel()
            engine.hub.remove(viewer)

    return app
