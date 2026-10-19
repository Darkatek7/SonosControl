// FlyDJ live brain view.
//
// ES module imported by FlyDjPage through IJSRuntime ("import", "./js/flydj-brain.js").
// Renders the neuron cloud (geometry from /api/flydj/geometry) with three.js and lights up
// neurons from the binary spike stream (/flydj/stream). Both are served by SonosControl.Web
// behind cookie authentication; see flydj/API.md for the binary layouts.
//
// Public API (all return plain values so they work through IJSObjectReference):
//   init(canvas, options) -> { id, webgl }
//   setReachable(id, reachable), setTheme(id), setReducedMotion(id, reduced), dispose(id)
// Status goes back to .NET at most once per second via options.dotNet.OnBrainStatus(geometry, stream).

import * as THREE from "../lib/three/three.module.min.js";
import { OrbitControls } from "../lib/three/OrbitControls.js";

const GEOMETRY_URL = "api/flydj/geometry";
const STREAM_URL = "flydj/stream";
const FRAME_MAGIC = 0xf1;
const FRAME_VERSION = 1;
const FRAME_HEADER_BYTES = 20;
const MIN_RECONNECT_MS = 2000;
const MAX_RECONNECT_MS = 30000;
const REPORT_INTERVAL_MS = 1000;
const HOVER_INTERVAL_MS = 90;
const AUTO_ROTATE_RESUME_MS = 5000;
const MAX_DEVICE_PIXEL_RATIO = 2;

const instances = new Map();
let nextId = 1;

// Populations that have no explicit CSS token still get a distinguishable colour.
const FALLBACK_POPULATION_COLORS = {
    jo_a: "#5aa5d0",
    jo_b: "#8d7ae0",
    jo_ce: "#b27fd6",
    approach: "#35b27f",
    startle: "#e0566a",
    arousal: "#e0a24a",
};
const FALLBACK_NEURON = "#8a7ca8";
const FALLBACK_SILHOUETTE = "#8a7ca8";
const FALLBACK_STAGE = "#100e1b";

/** Parses the FDJG geometry document (flydj/API.md, "GET /v1/model/geometry"). Exported for tests. */
export function parseGeometry(buffer) {
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < 8) throw new Error("geometry too short");
    const bytes = new Uint8Array(buffer);
    if (bytes[0] !== 0x46 || bytes[1] !== 0x44 || bytes[2] !== 0x4a || bytes[3] !== 0x47) throw new Error("bad geometry magic");
    const view = new DataView(buffer);
    const headerLength = view.getUint32(4, true);
    if (8 + headerLength > buffer.byteLength) throw new Error("geometry header truncated");
    const header = JSON.parse(new TextDecoder("utf-8").decode(new Uint8Array(buffer, 8, headerLength)));
    if (header.version !== 1) throw new Error("unsupported geometry version");
    const n = header.neuronCount | 0;
    const s = header.silhouetteCount | 0;
    if (n < 0 || s < 0) throw new Error("bad geometry counts");
    let offset = 8 + headerLength + ((4 - (headerLength % 4)) % 4);
    if (offset + n * 12 + s * 12 + n * 2 + n > buffer.byteLength) throw new Error("geometry truncated");
    const positions = new Float32Array(buffer, offset, n * 3);
    offset += n * 12;
    const silhouette = new Float32Array(buffer, offset, s * 3);
    offset += s * 12;
    const cellTypeIndex = new Uint16Array(buffer, offset, n);
    offset += n * 2;
    const population = new Uint8Array(buffer, offset, n);
    return { header, neuronCount: n, silhouetteCount: s, positions, silhouette, cellTypeIndex, population };
}

/** Parses one stream frame (flydj/API.md, "WS /v1/stream"); returns null for frames it does not understand. */
export function parseFrame(buffer) {
    if (!(buffer instanceof ArrayBuffer) || buffer.byteLength < FRAME_HEADER_BYTES) return null;
    const view = new DataView(buffer);
    if (view.getUint8(0) !== FRAME_MAGIC || view.getUint8(1) !== FRAME_VERSION) return null;
    const populationCount = view.getUint16(2, true);
    const spikeCount = view.getUint32(4, true);
    const spikeOffset = FRAME_HEADER_BYTES + 4 * populationCount;
    if (spikeOffset + 4 * spikeCount > buffer.byteLength) return null;
    return {
        bioMs: view.getFloat64(8, true),
        engagement: view.getFloat32(16, true),
        rates: new Float32Array(buffer, FRAME_HEADER_BYTES, populationCount),
        spikes: new Uint32Array(buffer, spikeOffset, spikeCount),
    };
}

let probeContext = null;
/** Resolves any CSS colour (including color-mix) to [r, g, b] in 0..255 by painting one pixel. */
function resolveColor(value, fallback) {
    const text = (value || "").trim() || fallback;
    try {
        if (!probeContext) {
            const probe = document.createElement("canvas");
            probe.width = probe.height = 1;
            probeContext = probe.getContext("2d", { willReadFrequently: true });
        }
        probeContext.clearRect(0, 0, 1, 1);
        probeContext.fillStyle = "#000000";
        probeContext.fillStyle = text;
        probeContext.fillRect(0, 0, 1, 1);
        const d = probeContext.getImageData(0, 0, 1, 1).data;
        return [d[0] / 255, d[1] / 255, d[2] / 255];
    } catch {
        return resolveColor(fallback, "#888888");
    }
}

function luminance([r, g, b]) {
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

const POINT_VERTEX = /* glsl */ `
    attribute vec3 aColor;
    attribute vec2 aStyle;     // x: base size in px, y: base alpha
    attribute float aActivity; // 0..1, decays on the CPU
    uniform float uPixelRatio;
    uniform float uScale;
    uniform float uDark;
    varying vec3 vColor;
    varying float vAlpha;
    varying float vActivity;
    void main() {
        vec4 mv = modelViewMatrix * vec4(position, 1.0);
        float size = aStyle.x * (1.0 + 2.2 * aActivity);
        gl_PointSize = clamp(size * uPixelRatio * (uScale / max(-mv.z, 1.0)), 1.0, 28.0 * uPixelRatio);
        gl_Position = projectionMatrix * mv;
        vActivity = aActivity;
        vAlpha = aStyle.y + (1.0 - aStyle.y) * min(aActivity * 1.4, 1.0);
        vColor = uDark > 0.5
            ? mix(aColor, vec3(1.0), aActivity * 0.55)
            : mix(aColor, aColor * 0.55, aActivity * 0.6);
    }
`;

const POINT_FRAGMENT = /* glsl */ `
    varying vec3 vColor;
    varying float vAlpha;
    varying float vActivity;
    void main() {
        float d = length(gl_PointCoord - vec2(0.5));
        if (d > 0.5) discard;
        float soft = smoothstep(0.5, 0.05, d);
        gl_FragColor = vec4(vColor, vAlpha * soft);
    }
`;

const SILHOUETTE_VERTEX = /* glsl */ `
    uniform float uPixelRatio;
    uniform float uScale;
    void main() {
        vec4 mv = modelViewMatrix * vec4(position, 1.0);
        gl_PointSize = clamp(1.4 * uPixelRatio * (uScale / max(-mv.z, 1.0)), 1.0, 6.0 * uPixelRatio);
        gl_Position = projectionMatrix * mv;
    }
`;

const SILHOUETTE_FRAGMENT = /* glsl */ `
    uniform vec3 uColor;
    uniform float uAlpha;
    void main() {
        float d = length(gl_PointCoord - vec2(0.5));
        if (d > 0.5) discard;
        gl_FragColor = vec4(uColor, uAlpha * smoothstep(0.5, 0.1, d));
    }
`;

function webglAvailable() {
    try {
        const probe = document.createElement("canvas");
        return !!(probe.getContext("webgl2") || probe.getContext("webgl"));
    } catch {
        return false;
    }
}

class BrainView {
    constructor(id, canvas, options) {
        this.id = id;
        this.canvas = canvas;
        this.stage = canvas.parentElement || canvas;
        this.tooltip = options.tooltip || null;
        this.dotNet = options.dotNet || null;
        this.reachable = options.reachable !== false;
        this.reducedMotionOverride = typeof options.reducedMotion === "boolean" ? options.reducedMotion : null;
        this.geometryUrl = new URL(options.geometryUrl || GEOMETRY_URL, document.baseURI).href;
        this.streamUrl = (() => {
            const url = new URL(options.streamUrl || STREAM_URL, document.baseURI);
            url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
            return url.href;
        })();

        this.disposed = false;
        this.visible = true;
        this.geometryState = "loading"; // loading | ready | failed
        this.streamState = "idle"; // idle | connecting | live | offline
        this.geometry = null;
        this.activity = null;
        this.hot = false;
        this.rafId = 0;
        this.lastFrameTime = 0;
        this.socket = null;
        this.reconnectDelay = MIN_RECONNECT_MS;
        this.reconnectTimer = 0;
        this.geometryRetryDelay = MIN_RECONNECT_MS;
        this.geometryTimer = 0;
        this.geometryAbort = null;
        this.resumeRotateTimer = 0;
        this.reportTimer = 0;
        this.reportDirty = false;
        this.lastReport = "";
        this.lastHover = 0;
        this.hoverIndex = -1;
        this.cameraDistance = 400;
        this.listeners = [];

        this.renderer = new THREE.WebGLRenderer({ canvas, antialias: false, alpha: false, powerPreference: "default" });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, MAX_DEVICE_PIXEL_RATIO));
        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(45, 1, 1, 100000);
        this.camera.position.set(0, 0, this.cameraDistance);
        this.controls = new OrbitControls(this.camera, canvas);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.08;
        this.controls.enablePan = false;
        this.controls.rotateSpeed = 0.7;
        this.controls.zoomSpeed = 0.8;
        // OrbitControls claims every touch; keep vertical swipes scrolling the page on phones.
        canvas.style.touchAction = "pan-y";
        this.controls.autoRotateSpeed = 0.9;
        this.raycaster = new THREE.Raycaster();
        this.pointer = new THREE.Vector2();

        this.uniforms = {
            uPixelRatio: { value: this.renderer.getPixelRatio() },
            uScale: { value: this.cameraDistance },
            uDark: { value: 1 },
        };
        this.silhouetteUniforms = {
            uPixelRatio: this.uniforms.uPixelRatio,
            uScale: this.uniforms.uScale,
            uColor: { value: new THREE.Vector3(0.5, 0.5, 0.6) },
            uAlpha: { value: 0.1 },
        };

        this.applyMotionPreference();
        this.readTheme();
        this.bindEvents();
        this.resize();
        this.sync();
    }

    get reducedMotion() {
        if (this.reducedMotionOverride !== null) return this.reducedMotionOverride;
        return !!window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
    }

    on(target, type, handler, options) {
        target.addEventListener(type, handler, options);
        this.listeners.push(() => target.removeEventListener(type, handler, options));
    }

    bindEvents() {
        if (typeof ResizeObserver === "function") {
            this.resizeObserver = new ResizeObserver(() => this.resize());
            this.resizeObserver.observe(this.stage);
        }
        if (typeof IntersectionObserver === "function") {
            this.intersectionObserver = new IntersectionObserver((entries) => {
                const entry = entries[entries.length - 1];
                if (entry) this.setVisible(entry.isIntersecting);
            });
            this.intersectionObserver.observe(this.canvas);
        }
        this.on(document, "visibilitychange", () => this.sync());
        this.on(this.canvas, "webglcontextlost", (event) => event.preventDefault());
        this.on(this.canvas, "webglcontextrestored", () => this.requestFrame());
        this.on(this.canvas, "pointermove", (event) => this.onPointerMove(event));
        this.on(this.canvas, "pointerleave", () => this.hideTooltip());
        this.on(this.canvas, "pointerdown", () => this.hideTooltip());
        this.controls.addEventListener("start", () => {
            clearTimeout(this.resumeRotateTimer);
            this.controls.autoRotate = false;
            this.requestFrame();
        });
        this.controls.addEventListener("end", () => {
            clearTimeout(this.resumeRotateTimer);
            this.resumeRotateTimer = setTimeout(() => {
                this.controls.autoRotate = !this.reducedMotion;
                this.requestFrame();
            }, AUTO_ROTATE_RESUME_MS);
        });
        this.controls.addEventListener("change", () => this.requestFrame());

        const reducedQuery = window.matchMedia?.("(prefers-reduced-motion: reduce)");
        if (reducedQuery?.addEventListener) this.on(reducedQuery, "change", () => this.applyMotionPreference());
        // sonos-theme.js flips data-theme (also for the "system" preference), so one observer covers every case.
        this.themeObserver = new MutationObserver(() => this.setTheme());
        this.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    }

    applyMotionPreference() {
        this.controls.autoRotate = !this.reducedMotion;
        this.decayTau = this.reducedMotion ? 0.9 : 0.35;
        this.requestFrame();
    }

    // ---- theme -------------------------------------------------------------------------

    readTheme() {
        const style = getComputedStyle(this.canvas);
        const token = (name, fallback) => resolveColor(style.getPropertyValue(name), fallback);
        this.stageColor = token("--flydj-stage-bg", FALLBACK_STAGE);
        this.neuronColor = token("--flydj-neuron", FALLBACK_NEURON);
        this.silhouetteColor = token("--flydj-silhouette", FALLBACK_SILHOUETTE);
        this.dark = luminance(this.stageColor) < 0.45;
        this.populationColors = new Map();
        const populations = this.geometry?.header.populations ?? [];
        for (const population of populations) {
            const fallback = FALLBACK_POPULATION_COLORS[population.key] || "#b0a2c7";
            this.populationColors.set(population.id, token(`--flydj-pop-${population.key}`, fallback));
        }

        const [r, g, b] = this.stageColor;
        this.renderer.setClearColor(new THREE.Color().setRGB(r, g, b, THREE.SRGBColorSpace), 1);
        this.uniforms.uDark.value = this.dark ? 1 : 0;
        this.silhouetteUniforms.uColor.value.set(...this.silhouetteColor);
        this.silhouetteUniforms.uAlpha.value = this.dark ? 0.12 : 0.32;
        if (this.points) this.points.material.blending = this.dark ? THREE.AdditiveBlending : THREE.NormalBlending;
        if (this.silhouettePoints) this.silhouettePoints.material.blending = this.dark ? THREE.AdditiveBlending : THREE.NormalBlending;
        this.paintColors();
    }

    paintColors() {
        if (!this.geometry || !this.colorAttribute) return;
        const { population } = this.geometry;
        const colors = this.colorAttribute.array;
        for (let i = 0; i < population.length; i += 1) {
            const color = this.populationColors.get(population[i]) || this.neuronColor;
            colors[i * 3] = color[0];
            colors[i * 3 + 1] = color[1];
            colors[i * 3 + 2] = color[2];
        }
        const styles = this.styleAttribute.array;
        for (let i = 0; i < population.length; i += 1) {
            const named = population[i] !== 0;
            styles[i * 2] = named ? 3.4 : 1.7;
            styles[i * 2 + 1] = named ? 0.9 : this.dark ? 0.28 : 0.5;
        }
        this.colorAttribute.needsUpdate = true;
        this.styleAttribute.needsUpdate = true;
        this.requestFrame();
    }

    setTheme() {
        if (this.disposed) return;
        // Custom properties change with the attribute, but transitions on other properties may still run.
        requestAnimationFrame(() => {
            if (this.disposed) return;
            this.readTheme();
        });
    }

    // ---- sizing and rendering -----------------------------------------------------------

    resize() {
        if (this.disposed) return;
        const rect = this.stage.getBoundingClientRect();
        const width = Math.max(0, Math.floor(rect.width));
        const height = Math.max(0, Math.floor(rect.height));
        if (width < 2 || height < 2) return;
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, MAX_DEVICE_PIXEL_RATIO));
        this.uniforms.uPixelRatio.value = this.renderer.getPixelRatio();
        this.renderer.setSize(width, height, false);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
        this.requestFrame();
    }

    setVisible(visible) {
        this.visible = visible;
        this.sync();
    }

    get running() {
        return !this.disposed && this.visible && !document.hidden;
    }

    requestFrame() {
        if (this.disposed || this.rafId || !this.running) return;
        this.rafId = requestAnimationFrame((time) => this.frame(time));
    }

    frame(time) {
        this.rafId = 0;
        if (!this.running) {
            this.lastFrameTime = 0;
            return;
        }
        const dt = this.lastFrameTime ? Math.min(0.1, Math.max(0.001, (time - this.lastFrameTime) / 1000)) : 0.016;
        this.lastFrameTime = time;
        this.decayActivity(dt);
        const moved = this.controls.update(dt);
        this.uniforms.uScale.value = this.cameraDistance;
        this.renderer.render(this.scene, this.camera);
        // requestFrame de-duplicates with the "change" event that controls.update() may have just fired.
        if (this.controls.autoRotate || this.hot || moved) this.requestFrame();
    }

    decayActivity(dt) {
        if (!this.hot || !this.activity) return;
        const factor = Math.exp(-dt / this.decayTau);
        const values = this.activity;
        let peak = 0;
        for (let i = 0; i < values.length; i += 1) {
            const value = values[i];
            if (value === 0) continue;
            const next = value * factor;
            values[i] = next < 0.01 ? 0 : next;
            if (next > peak) peak = next;
        }
        this.activityAttribute.needsUpdate = true;
        if (peak < 0.01) this.hot = false;
    }

    // ---- geometry ----------------------------------------------------------------------

    async loadGeometry() {
        if (this.disposed || this.geometry || this.geometryAbort || !this.reachable) return;
        const abort = new AbortController();
        this.geometryAbort = abort;
        this.setGeometryState("loading");
        try {
            const response = await fetch(this.geometryUrl, { cache: "no-cache", credentials: "same-origin", signal: abort.signal });
            if (!response.ok) throw new Error(`geometry ${response.status}`);
            const parsed = parseGeometry(await response.arrayBuffer());
            if (this.disposed || abort.signal.aborted) return;
            this.geometryAbort = null;
            this.buildScene(parsed);
            this.geometryRetryDelay = MIN_RECONNECT_MS;
            this.setGeometryState("ready");
            this.sync();
        } catch {
            if (this.geometryAbort === abort) this.geometryAbort = null;
            if (this.disposed || abort.signal.aborted) return;
            this.setGeometryState("failed");
            const delay = this.geometryRetryDelay;
            this.geometryRetryDelay = Math.min(MAX_RECONNECT_MS, delay * 2);
            clearTimeout(this.geometryTimer);
            this.geometryTimer = setTimeout(() => this.sync(), delay);
        }
    }

    buildScene(parsed) {
        this.geometry = parsed;
        const { positions, silhouette, neuronCount } = parsed;

        let radiusSquared = 1;
        for (let i = 0; i < positions.length; i += 3) {
            const d = positions[i] ** 2 + positions[i + 1] ** 2 + positions[i + 2] ** 2;
            if (d > radiusSquared) radiusSquared = d;
        }
        for (let i = 0; i < silhouette.length; i += 3) {
            const d = silhouette[i] ** 2 + silhouette[i + 1] ** 2 + silhouette[i + 2] ** 2;
            if (d > radiusSquared) radiusSquared = d;
        }
        const radius = Math.sqrt(radiusSquared);
        this.radius = radius;
        this.cameraDistance = radius * 2.2;
        this.camera.near = Math.max(0.1, radius * 0.05);
        this.camera.far = radius * 20;
        this.camera.position.set(radius * 0.55, radius * 0.4, this.cameraDistance);
        this.camera.updateProjectionMatrix();
        this.controls.minDistance = radius * 0.7;
        this.controls.maxDistance = radius * 6;
        this.controls.target.set(0, 0, 0);
        this.controls.update();
        this.cameraDistance = this.camera.position.length();

        const pointGeometry = new THREE.BufferGeometry();
        pointGeometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
        this.colorAttribute = new THREE.BufferAttribute(new Float32Array(neuronCount * 3), 3);
        this.styleAttribute = new THREE.BufferAttribute(new Float32Array(neuronCount * 2), 2);
        this.activity = new Float32Array(neuronCount);
        this.activityAttribute = new THREE.BufferAttribute(this.activity, 1);
        this.activityAttribute.setUsage(THREE.DynamicDrawUsage);
        pointGeometry.setAttribute("aColor", this.colorAttribute);
        pointGeometry.setAttribute("aStyle", this.styleAttribute);
        pointGeometry.setAttribute("aActivity", this.activityAttribute);
        pointGeometry.computeBoundingSphere();
        const material = new THREE.ShaderMaterial({
            uniforms: this.uniforms,
            vertexShader: POINT_VERTEX,
            fragmentShader: POINT_FRAGMENT,
            transparent: true,
            depthWrite: false,
            depthTest: false,
            blending: this.dark ? THREE.AdditiveBlending : THREE.NormalBlending,
        });
        this.points = new THREE.Points(pointGeometry, material);
        this.points.frustumCulled = false;

        const shellGeometry = new THREE.BufferGeometry();
        shellGeometry.setAttribute("position", new THREE.BufferAttribute(silhouette, 3));
        const shellMaterial = new THREE.ShaderMaterial({
            uniforms: this.silhouetteUniforms,
            vertexShader: SILHOUETTE_VERTEX,
            fragmentShader: SILHOUETTE_FRAGMENT,
            transparent: true,
            depthWrite: false,
            depthTest: false,
            blending: this.dark ? THREE.AdditiveBlending : THREE.NormalBlending,
        });
        this.silhouettePoints = new THREE.Points(shellGeometry, shellMaterial);
        this.silhouettePoints.frustumCulled = false;
        this.silhouettePoints.renderOrder = 0;
        this.points.renderOrder = 1;
        this.scene.add(this.silhouettePoints, this.points);

        this.readTheme(); // colours depend on the population list from the header
        this.resize();
        this.requestFrame();
    }

    // ---- live stream --------------------------------------------------------------------

    get wantsStream() {
        return this.running && this.reachable && this.geometryState === "ready";
    }

    sync() {
        if (this.disposed) return;
        if (this.reachable && !this.geometry && !this.geometryAbort) this.loadGeometry();
        if (this.wantsStream) this.connect();
        else this.closeStream("idle");
        if (this.running) this.requestFrame();
    }

    connect() {
        if (this.socket || this.reconnectTimer || !this.wantsStream) return;
        let socket;
        try {
            socket = new WebSocket(this.streamUrl);
        } catch {
            this.scheduleReconnect();
            return;
        }
        socket.binaryType = "arraybuffer";
        this.socket = socket;
        this.setStreamState("connecting");
        socket.onopen = () => {
            if (this.socket !== socket) return;
            this.reconnectDelay = MIN_RECONNECT_MS;
            this.setStreamState("live");
        };
        socket.onmessage = (event) => {
            if (this.socket === socket) this.onFrame(event.data);
        };
        socket.onerror = () => { /* the close event follows; browsers log handshake failures themselves */ };
        socket.onclose = () => {
            if (this.socket !== socket) return;
            this.socket = null;
            this.setStreamState("offline");
            this.scheduleReconnect();
        };
    }

    scheduleReconnect() {
        if (this.disposed || this.reconnectTimer || !this.wantsStream) return;
        const delay = this.reconnectDelay;
        this.reconnectDelay = Math.min(MAX_RECONNECT_MS, delay * 2);
        this.reconnectTimer = setTimeout(() => {
            this.reconnectTimer = 0;
            this.sync();
        }, delay);
    }

    closeStream(state) {
        clearTimeout(this.reconnectTimer);
        this.reconnectTimer = 0;
        const socket = this.socket;
        this.socket = null;
        if (socket) {
            socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
            try { socket.close(); } catch { /* already closed */ }
        }
        this.setStreamState(state);
    }

    onFrame(data) {
        const frame = parseFrame(data);
        if (!frame || !this.activity || frame.spikes.length === 0) return;
        const limit = this.activity.length;
        const spikes = frame.spikes;
        for (let i = 0; i < spikes.length; i += 1) {
            const index = spikes[i];
            if (index < limit) this.activity[index] = 1;
        }
        this.activityAttribute.needsUpdate = true;
        this.hot = true;
        this.requestFrame();
    }

    // ---- hover tooltip ------------------------------------------------------------------

    onPointerMove(event) {
        if (event.pointerType === "touch" || !this.points || !this.tooltip || !this.running) return;
        if (event.buttons !== 0) return this.hideTooltip();
        const now = performance.now();
        if (now - this.lastHover < HOVER_INTERVAL_MS) return;
        this.lastHover = now;

        const rect = this.canvas.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) return;
        this.pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1);
        this.camera.updateMatrixWorld();
        this.raycaster.setFromCamera(this.pointer, this.camera);
        const distance = this.camera.position.length();
        const worldPerPixel = (2 * distance * Math.tan((this.camera.fov * Math.PI) / 360)) / Math.max(1, rect.height);
        this.raycaster.params.Points.threshold = worldPerPixel * 6;
        const hits = this.raycaster.intersectObject(this.points, false);
        if (hits.length === 0) return this.hideTooltip();

        let best = hits[0];
        for (const hit of hits) {
            const namedBest = this.geometry.population[best.index] !== 0;
            const named = this.geometry.population[hit.index] !== 0;
            if ((named && !namedBest) || (named === namedBest && hit.distanceToRay < best.distanceToRay)) best = hit;
        }
        this.showTooltip(best.index, event.clientX - rect.left, event.clientY - rect.top, rect);
    }

    showTooltip(index, x, y, rect) {
        const { header, cellTypeIndex, population } = this.geometry;
        const cell = header.cellTypes?.[cellTypeIndex[index]] || "Unclassified neuron";
        const populationId = population[index];
        const named = populationId === 0 ? null : header.populations.find((entry) => entry.id === populationId);
        const text = named ? `${cell} · ${named.label}` : cell;
        const tooltip = this.tooltip;
        if (this.hoverIndex !== index) {
            tooltip.textContent = text;
            this.hoverIndex = index;
        }
        tooltip.classList.add("is-visible");
        const width = tooltip.offsetWidth;
        const height = tooltip.offsetHeight;
        const left = Math.min(Math.max(4, x + 14), Math.max(4, rect.width - width - 4));
        const top = Math.min(Math.max(4, y + 14), Math.max(4, rect.height - height - 4));
        tooltip.style.left = `${left}px`;
        tooltip.style.top = `${top}px`;
    }

    hideTooltip() {
        this.hoverIndex = -1;
        this.tooltip?.classList.remove("is-visible");
    }

    // ---- status back to .NET (at most once per second) ----------------------------------

    setGeometryState(state) {
        if (this.geometryState === state) return;
        this.geometryState = state;
        this.report();
    }

    setStreamState(state) {
        if (this.streamState === state) return;
        this.streamState = state;
        this.report();
    }

    report() {
        if (this.disposed || !this.dotNet) return;
        if (this.reportTimer) {
            this.reportDirty = true;
            return;
        }
        this.flushReport();
        this.reportTimer = setTimeout(() => {
            this.reportTimer = 0;
            if (this.reportDirty) {
                this.reportDirty = false;
                this.report();
            }
        }, REPORT_INTERVAL_MS);
    }

    flushReport() {
        const key = `${this.geometryState}|${this.streamState}`;
        if (key === this.lastReport) return;
        this.lastReport = key;
        try {
            const result = this.dotNet.invokeMethodAsync("OnBrainStatus", this.geometryState, this.streamState);
            result?.catch?.(() => { /* circuit gone */ });
        } catch { /* circuit gone */ }
    }

    // ---- lifecycle ----------------------------------------------------------------------

    setReachable(reachable) {
        if (this.disposed || this.reachable === reachable) return;
        this.reachable = reachable;
        if (reachable) {
            this.geometryRetryDelay = MIN_RECONNECT_MS;
            this.reconnectDelay = MIN_RECONNECT_MS;
            clearTimeout(this.geometryTimer);
        } else {
            this.geometryAbort?.abort();
            this.geometryAbort = null;
            if (!this.geometry) this.setGeometryState("loading");
        }
        this.sync();
    }

    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        cancelAnimationFrame(this.rafId);
        clearTimeout(this.reconnectTimer);
        clearTimeout(this.geometryTimer);
        clearTimeout(this.resumeRotateTimer);
        clearTimeout(this.reportTimer);
        this.geometryAbort?.abort();
        this.closeStream("idle");
        this.resizeObserver?.disconnect();
        this.intersectionObserver?.disconnect();
        this.themeObserver?.disconnect();
        for (const remove of this.listeners) remove();
        this.listeners = [];
        this.controls.dispose();
        for (const object of [this.points, this.silhouettePoints]) {
            object?.geometry.dispose();
            object?.material.dispose();
        }
        this.renderer.dispose();
        this.renderer.forceContextLoss();
        this.hideTooltip();
        this.dotNet = null;
        this.tooltip = null;
    }
}

/** Creates a view on the canvas. Resolves { id, webgl }; webgl=false means the page should show its 2D fallback. */
export function init(canvas, options) {
    options = options || {};
    if (!canvas || !webglAvailable()) return { id: 0, webgl: false };
    try {
        const id = nextId++;
        const view = new BrainView(id, canvas, options);
        instances.set(id, view);
        return { id, webgl: true };
    } catch {
        return { id: 0, webgl: false };
    }
}

export function setReachable(id, reachable) {
    instances.get(id)?.setReachable(!!reachable);
}

export function setTheme(id) {
    instances.get(id)?.setTheme();
}

export function setReducedMotion(id, reduced) {
    const view = instances.get(id);
    if (!view) return;
    view.reducedMotionOverride = reduced === null || reduced === undefined ? null : !!reduced;
    view.applyMotionPreference();
}

export function dispose(id) {
    const view = instances.get(id);
    if (!view) return;
    instances.delete(id);
    view.dispose();
}
