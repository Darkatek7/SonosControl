import os
import html
import json
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import platform
import re
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from urllib.error import URLError, HTTPError
from urllib.request import urlopen

from playwright.sync_api import sync_playwright, expect, Error


BASE_URL = os.getenv("MOBILE_SMOKE_BASE_URL", "http://localhost:5107")
USERNAME = os.getenv("MOBILE_SMOKE_USERNAME")
PASSWORD = os.getenv("MOBILE_SMOKE_PASSWORD")
ADMIN_USERNAME = os.getenv("ADMIN_USERNAME")
ADMIN_PASSWORD = os.getenv("ADMIN_PASSWORD")
SERVER_START_TIMEOUT_SECONDS = int(os.getenv("MOBILE_SMOKE_SERVER_TIMEOUT", "180"))
AUTO_START_SERVER = os.getenv("MOBILE_SMOKE_AUTOSTART", "1") != "0"
MAX_LOGIN_ATTEMPTS = int(os.getenv("MOBILE_SMOKE_MAX_LOGIN_ATTEMPTS", "4"))
CHROME_PATH = os.getenv("PLAYWRIGHT_CHROME_PATH")
VIEWPORTS = [
    ("mobile", 390, 844),
    ("tablet", 768, 900),
    ("desktop", 1280, 900),
]
THEMES = ["light", "dark"]

ROUTES = [
    ("/", "home", "Quick access"),
    ("/library", "library", "Library"),
    ("/automation", "automation", "Schedules"),
    ("/insights", "insights", "Insights"),
    ("/administration/devices", "devices", "Devices"),
    ("/administration/settings", "settings", "System settings"),
    ("/administration/users", "users", "Accounts"),
    ("/administration/backups", "backups", "Backups"),
]


class SimulatedSpeakerHandler(BaseHTTPRequestHandler):
    transport_state = "PAUSED_PLAYBACK"
    volume = 34
    device_command_count = 0
    state_lock = threading.Lock()

    @classmethod
    def reset(cls):
        with cls.state_lock:
            cls.transport_state = "PAUSED_PLAYBACK"
            cls.volume = 34
            cls.device_command_count = 0

    @classmethod
    def set_external_state(cls, *, playing=None, volume=None):
        """Change the simulated room without sending a command from the app."""
        with cls.state_lock:
            if playing is not None:
                cls.transport_state = "PLAYING" if playing else "PAUSED_PLAYBACK"
            if volume is not None:
                cls.volume = max(0, min(100, int(volume)))

    @classmethod
    def command_count(cls):
        with cls.state_lock:
            return cls.device_command_count

    def log_message(self, *args):
        pass

    def respond(self, body):
        payload = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/xml; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        self.respond('<root><device><friendlyName>Test room</friendlyName><UDN>uuid:RINCON_00000000000101400</UDN><serviceList/></device></root>')

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode("utf-8")
        action = self.headers.get("SOAPACTION", "").strip('"').split("#")[-1]
        cls = type(self)
        if action not in {"GetVolume", "GetTransportInfo", "GetPositionInfo", "GetMediaInfo", "Browse", "GetZoneGroupState"}:
            with cls.state_lock:
                cls.device_command_count += 1
                if action == "Play":
                    cls.transport_state = "PLAYING"
                elif action in ("Pause", "Stop"):
                    cls.transport_state = "PAUSED_PLAYBACK" if action == "Pause" else "STOPPED"
                elif action == "SetVolume":
                    match = re.search(r"<DesiredVolume>(\d+)</DesiredVolume>", body)
                    if match:
                        cls.volume = int(match.group(1))
        with cls.state_lock:
            volume = cls.volume
            transport_state = cls.transport_state
        # A successful HTTP response with non-image content reproduces a broken Sonos cover.
        metadata = '<DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"><item><dc:title>Smoke test track</dc:title><upnp:artist>Local fixture</upnp:artist><upnp:albumArtURI>http://127.0.0.1:1400/missing-art</upnp:albumArtURI></item></DIDL-Lite>'
        topology = '<ZoneGroups><ZoneGroup Coordinator="RINCON_00000000000101400"><ZoneGroupMember UUID="RINCON_00000000000101400" Location="http://127.0.0.1:1400/xml/device_description.xml" /></ZoneGroup></ZoneGroups>'
        fields = {
            "GetVolume": f"<CurrentVolume>{volume}</CurrentVolume>",
            "GetTransportInfo": f"<CurrentTransportState>{transport_state}</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>",
            "GetPositionInfo": f"<Track>1</Track><TrackDuration>00:04:00</TrackDuration><TrackMetaData>{html.escape(metadata)}</TrackMetaData><TrackURI>http://127.0.0.1:1400/stream</TrackURI><RelTime>00:01:00</RelTime>",
            "GetMediaInfo": "<CurrentURI>http://127.0.0.1:1400/stream</CurrentURI>",
            "Browse": "<Result></Result><NumberReturned>0</NumberReturned><TotalMatches>0</TotalMatches><UpdateID>1</UpdateID>",
            "GetZoneGroupState": f"<ZoneGroupState>{html.escape(topology)}</ZoneGroupState>",
        }.get(action, "")
        service = "RenderingControl" if "Volume" in action else "AVTransport"
        self.respond(f'<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:{action}Response xmlns:u="urn:schemas-upnp-org:service:{service}:1">{fields}</u:{action}Response></s:Body></s:Envelope>')


def is_server_reachable():
    try:
        with urlopen(f"{BASE_URL}/auth/login", timeout=3):
            return True
    except (URLError, HTTPError, TimeoutError, OSError):
        return False


def wait_for_server_ready(timeout_seconds, process=None):
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if process and process.poll() is not None:
            return False
        if is_server_reachable():
            return True
        time.sleep(1)
    return False


def start_local_server(with_speaker=False):
    artifacts_dir = Path("artifacts")
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    log_path = artifacts_dir / "mobile_smoke_server.log"
    log_stream = log_path.open("w", encoding="utf-8")
    runtime_dir = Path(tempfile.mkdtemp(prefix="sonoscontrol-smoke-", dir=artifacts_dir)).resolve()
    settings_dir = runtime_dir / "settings"
    settings_dir.mkdir(parents=True, exist_ok=True)
    settings = {
        "SettingsSchemaVersion": 2,
        "IP_Adress": "127.0.0.1" if with_speaker else "",
        "Speakers": [{"Name": "Test room", "IpAddress": "127.0.0.1", "Uuid": "RINCON_00000000000101400"}] if with_speaker else [],
        "Scenes": ([{
            "Id": "legacy-smoke-scene", "Name": "Legacy smoke scene", "Enabled": False,
            "SourceSelectionMode": 1, "SourceType": 0, "IsSyncedPlayback": False,
            "SpeakerIps": ["127.0.0.1"], "Actions": [],
        }] if with_speaker else []),
        "Volume": 34,
        "Stations": [{"Name": "Test radio", "Url": "http://127.0.0.1:1400/stream"}],
        "SpotifyTracks": [], "YouTubeCollections": [], "YouTubeMusicCollections": [],
    }
    (settings_dir / "config.json").write_text(json.dumps(settings), encoding="utf-8")

    process = subprocess.Popen(
        ["dotnet", "run", "--project", "SonosControl.Web", "--no-build", "--urls", BASE_URL],
        stdout=log_stream,
        stderr=subprocess.STDOUT,
        cwd=Path(__file__).resolve().parent,
        shell=False,
        env={
            **os.environ,
            "BackgroundServices__Enabled": "false",
            "Automation__TimeZone": "UTC",
            "PlaybackSnapshots__Enabled": "true" if with_speaker else "false",
            "Settings__DataDirectory": str(settings_dir),
            "ConnectionStrings__DefaultConnection": f"Data Source={runtime_dir / 'app.db'}",
            "ADMIN_USERNAME": os.getenv("ADMIN_USERNAME", "admin"),
            "ADMIN_EMAIL": os.getenv("ADMIN_EMAIL", "admin@smoke.invalid"),
            "ADMIN_PASSWORD": os.getenv("ADMIN_PASSWORD", "Test1234."),
            "DataProtection__KeysDirectory": str(runtime_dir / "keys"),
        },
    )
    return process, log_stream, log_path, runtime_dir


def stop_local_server(process, log_stream, runtime_dir=None):
    if process and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    if log_stream:
        log_stream.close()
    if runtime_dir:
        shutil.rmtree(runtime_dir, ignore_errors=True)


def assert_no_horizontal_overflow(page):
    dimensions = page.evaluate(
        """
        () => ({
            viewportWidth: window.innerWidth,
            documentWidth: document.documentElement.scrollWidth,
            bodyWidth: document.body.scrollWidth,
        })
        """
    )
    assert dimensions["documentWidth"] <= dimensions["viewportWidth"], (
        f"{page.url} has horizontal overflow: viewport={dimensions['viewportWidth']}px, "
        f"document={dimensions['documentWidth']}px, body={dimensions['bodyWidth']}px."
    )


def resolve_chrome_path():
    if CHROME_PATH:
        path = Path(CHROME_PATH)
        if path.exists():
            return str(path)
        raise RuntimeError(f"PLAYWRIGHT_CHROME_PATH does not exist: {CHROME_PATH}")

    if platform.system() == "Darwin":
        mac_chrome = Path("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")
        if mac_chrome.exists():
            return str(mac_chrome)

    return None


def launch_chromium(playwright):
    executable_path = resolve_chrome_path()
    if executable_path:
        return playwright.chromium.launch(headless=True, executable_path=executable_path)

    return playwright.chromium.launch(headless=True)


def assert_global_player_visible(page):
    player = page.locator("[data-qa='global-player-bar']")
    expect(player).to_have_count(1)
    expect(player).to_be_visible(timeout=10000)
    expect(player).to_have_class(re.compile(r"\bplayer-surface--compact\b"))


def assert_player_state(page, with_speaker):
    if with_speaker:
        assert_global_player_visible(page)
        assert_bottom_player_does_not_cover_content(page)
        expect(page.locator(".app-shell--without-player")).to_have_count(0)
    else:
        expect(page.locator("[data-qa='global-player-bar'], .player-sheet")).to_have_count(0)
        expect(page.locator(".app-shell--without-player")).to_have_count(1)
        padding = page.locator("article.content").evaluate("element => parseFloat(getComputedStyle(element).paddingBottom)")
        assert padding < 80, "Empty configuration must not reserve a playback dock."


def assert_home_onboarding(page):
    expect(page.locator("[data-qa='home-setup']")).to_be_visible(timeout=10000)
    expect(page.get_by_role("heading", name="Connect your first speaker", exact=True)).to_be_visible()
    expect(page.locator(".home-primary-grid, .home-quick-library, .home-footer, .home-intro")).to_have_count(0)
    expect(page.locator(".home-setup__steps li")).to_have_count(3)
    action = page.get_by_role("link", name="Set up speakers", exact=True)
    expect(action).to_have_attribute("href", "/administration/devices")
    assert action.bounding_box()["height"] >= 44


def assert_bottom_player_does_not_cover_content(page):
    spacing = page.evaluate(
        """
        () => {
            const player = document.querySelector('[data-qa="global-player-bar"]');
            const content = document.querySelector('article.content');
            if (!player || !content) return null;
            const playerRect = player.getBoundingClientRect();
            const playerStyle = window.getComputedStyle(player);
            const paddingBottom = Number.parseFloat(window.getComputedStyle(content).paddingBottom) || 0;
            return {
                bottomGap: window.innerHeight - playerRect.bottom,
                bottomInset: Number.parseFloat(playerStyle.bottom) || 0,
                bottomMargin: Number.parseFloat(playerStyle.marginBottom) || 0,
                paddingBottom,
                playerHeight: playerRect.height,
                position: playerStyle.position
            };
        }
        """
    )
    assert spacing is not None, "Global bottom player or content region missing."
    assert spacing["position"] in ("fixed", "sticky"), "Global player must stay anchored while scrolling."
    assert (
        spacing["bottomInset"] - 2 <= spacing["bottomGap"]
        <= spacing["bottomInset"] + spacing["bottomMargin"] + 2
    ), "Global player is not anchored at its configured dock inset."
    if spacing["position"] == "fixed":
        assert abs(spacing["bottomGap"] - spacing["bottomInset"]) <= 2, "Fixed player drifted from its dock inset."
        assert (
            spacing["paddingBottom"] + 4 >= spacing["playerHeight"] + spacing["bottomGap"]
        ), "Main content does not reserve the global player's height and dock inset."


def apply_theme(page, theme):
    page.evaluate("theme => window.sonosTheme.apply(theme)", theme)
    # Let the existing color transitions settle before inspecting or capturing.
    page.wait_for_timeout(250)


def assert_home_dashboard_layout(page):
    expect(page.locator("[data-qa='home-dashboard']")).to_be_visible(timeout=10000)
    expect(page.get_by_role("heading", name="Quick access")).to_be_visible(timeout=10000)
    expect(page.get_by_role("heading", name="Active Automation")).to_be_visible(timeout=10000)
    expect(page.get_by_role("heading", name="Device warnings")).to_be_visible(timeout=10000)
    expect(page.locator(".spotify-library")).to_have_count(0)
    expect(page.locator(".spotify-home-context")).to_have_count(0)
    expect(page.locator(".spotify-room-picker")).to_have_count(0)
    expect(page.locator(".player-surface--expanded")).to_have_count(0)
    expect(page.locator("[data-qa='global-player-sync']")).to_have_count(1)
    assert page.locator(".home-quick-library .library__item").count() <= 6
    if page.locator("[data-qa='room-card']").count() > 0:
        expect(page.get_by_role("heading", name="Speakers")).to_be_visible(timeout=10000)


def assert_library_cards_are_uniform(page):
    cards = page.locator(".source-card")
    if cards.count() == 0:
        return

    dimensions = cards.evaluate_all(
        """
        elements => elements.map(element => {
            const rect = element.getBoundingClientRect();
            return { width: rect.width, height: rect.height };
        })
        """
    )
    heights = [round(item["height"], 2) for item in dimensions]
    assert max(heights) - min(heights) <= 1, f"Library card heights differ: {heights}"
    favourite_buttons = page.locator(".source-card__favourite")
    expect(favourite_buttons).to_have_count(cards.count())
    button_sizes = favourite_buttons.evaluate_all(
        "elements => elements.map(element => element.getBoundingClientRect().width)"
    )
    assert all(size >= 44 for size in button_sizes), f"Favourite touch targets are too small: {button_sizes}"


def verify_responsive_home(page, output_dir, with_speaker=True):
    for slug, width, height in VIEWPORTS:
        for theme in THEMES:
            page.set_viewport_size({"width": width, "height": height})
            page.goto(f"{BASE_URL}/", wait_until="networkidle")
            apply_theme(page, theme)
            assert_player_state(page, with_speaker)
            if not with_speaker:
                assert_home_onboarding(page)
                assert_no_horizontal_overflow(page)
                page.screenshot(path=str(output_dir / f"home_{slug}_{theme}_setup.png"), full_page=True)
                continue
            assert_home_dashboard_layout(page)
            if width >= 1200:
                expect(page.locator("#global-player-volume-number")).to_be_visible(timeout=10000)
            if width <= 768:
                page.get_by_role("button", name="Open expanded player").click()
                sheet = page.get_by_role("dialog", name="Now playing")
                expect(sheet).to_be_visible(timeout=10000)
                expect(sheet.get_by_label("Room", exact=True)).to_be_visible()
                expect(sheet.get_by_label("Volume for active room percentage")).to_be_visible()
                expect(sheet.get_by_role("button", name="Sync", exact=True)).to_be_visible()
                expect(sheet.get_by_role("heading", name="Queue")).to_be_visible()
                sheet.get_by_role("button", name="Close expanded player").click()
            assert_no_horizontal_overflow(page)
            assert_bottom_player_does_not_cover_content(page)
            page.screenshot(path=str(output_dir / f"home_{slug}_{theme}.png"), full_page=True)


def verify_drawer(page):
    menu_button = page.locator("button.app-mobile-menu-button")
    if menu_button.count() == 0:
        return

    expect(menu_button.first).to_be_visible(timeout=5000)
    menu_button.first.click(force=True)
    page.wait_for_timeout(150)
    expect(page.locator(".nav-scrollable")).to_be_visible(timeout=5000)
    expect(page.get_by_role("link", name="Home", exact=True)).to_be_visible(timeout=5000)
    expect(page.get_by_role("link", name="Library", exact=True)).to_be_visible(timeout=5000)
    expect(page.get_by_role("link", name="Automation", exact=True)).to_be_visible(timeout=5000)
    expect(page.get_by_role("link", name="Insights", exact=True)).to_be_visible(timeout=5000)
    page.locator("button.nav-drawer-close").click()
    expect(page.locator("#app-sidebar")).not_to_be_visible(timeout=5000)


def get_login_attempts():
    attempts = []
    seen = set()

    usernames = []
    passwords = []

    if USERNAME:
        usernames.append(USERNAME)
    if ADMIN_USERNAME:
        usernames.append(ADMIN_USERNAME)
    usernames.append("admin")

    if PASSWORD:
        passwords.append(PASSWORD)
    if ADMIN_PASSWORD:
        passwords.append(ADMIN_PASSWORD)
    passwords.append("Test1234.")

    for username in usernames:
        for password in passwords:
            if not username or not password:
                continue
            key = (username, password)
            if key in seen:
                continue
            seen.add(key)
            attempts.append(key)
            if len(attempts) >= MAX_LOGIN_ATTEMPTS:
                return attempts

    return attempts


def try_login(page, username, password):
    page.goto(f"{BASE_URL}/auth/login", wait_until="networkidle")
    page.fill("#username", username)
    page.fill("#password", password)
    page.click("button#loginBtn")
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(300)

    if "/auth/login" not in page.url:
        return True, page.url, ""

    error_text = ""
    error_alert = page.locator(".alert[role='alert']")
    if error_alert.count() > 0:
        error_text = error_alert.first.inner_text().strip()

    return False, page.url, error_text


def assert_control_is_clickable(control):
    expect(control).to_be_visible()
    control.scroll_into_view_if_needed()
    unobscured = control.evaluate("""element => {
        const rect = element.getBoundingClientRect();
        const x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
        return y >= 0 && y < innerHeight && x >= 0 && x < innerWidth
            && element.contains(document.elementFromPoint(x, y));
    }""")
    assert unobscured, f"Control is covered or outside the viewport: {control.inner_text()}"


def assert_editor_labels(dialog):
    missing = dialog.locator("input, select").evaluate_all("""elements => elements
        .filter(element => element.getBoundingClientRect().height > 0)
        .filter(element => !element.getAttribute('aria-label') && !element.getAttribute('aria-labelledby')
            && (!element.labels || element.labels.length === 0))
        .map(element => element.outerHTML)""")
    assert not missing, f"Unlabelled editor fields: {missing}"


def assert_focus_in_dialog(page, dialog):
    assert dialog.evaluate("element => element.contains(document.activeElement)"), "Focus escaped the active dialog."


def open_advanced(editor):
    advanced = editor.locator("[data-qa='schedule-advanced']")
    expect(advanced).to_have_count(1)
    if advanced.get_attribute("open") is None:
        advanced.locator("summary").click()
    expect(advanced.get_by_label("Priority", exact=True)).to_be_visible()
    return advanced


def verify_editor_sizes(page, editor, save_name, advanced_labels=()):
    for width, height, text_scale in [(390, 844, 100), (320, 640, 100), (390, 640, 200)]:
        page.set_viewport_size({"width": width, "height": height})
        page.evaluate("scale => document.documentElement.style.fontSize = scale + '%'", text_scale)
        for theme in THEMES:
            apply_theme(page, theme)
            if advanced_labels:
                advanced = open_advanced(editor)
                for label in advanced_labels:
                    assert_control_is_clickable(advanced.get_by_label(label, exact=True))
                assert_control_is_clickable(advanced.get_by_role("button", name="Add exclusion", exact=True))
            body = editor.locator(".app-dialog__body")
            body.evaluate("element => element.scrollTop = element.scrollHeight")
            assert body.evaluate("element => Math.abs(element.scrollHeight - element.clientHeight - element.scrollTop) <= 1"), "Editor form did not scroll to its end."
            assert_control_is_clickable(editor.get_by_role("button", name=save_name, exact=True))
            assert_no_horizontal_overflow(page)
    page.evaluate("document.documentElement.style.fontSize = ''")
    page.set_viewport_size({"width": 390, "height": 844})


def stop_rule_times():
    """Daily stop rule whose cutoff passed 15 minutes ago and releases in 3 hours (fixture time zone is UTC)."""
    now = datetime.now(timezone.utc)
    return (now - timedelta(minutes=15)).strftime("%H:%M"), (now + timedelta(hours=3)).strftime("%H:%M")


def inactive_schedule_times():
    """A window two to four hours ahead, so no schedule is active when the manual start happens."""
    now = datetime.now(timezone.utc)
    return (now + timedelta(hours=2)).strftime("%H:%M"), (now + timedelta(hours=4)).strftime("%H:%M")


def assert_stops_at(locator, expected_minutes_from_now):
    """Locator shows 'Stops at HH:MM · <reason>' close to the expected wall-clock time."""
    expect(locator).to_contain_text(re.compile(r"Stops at \d{2}:\d{2} · \S"), timeout=15000)
    match = re.search(r"Stops at (\d{2}):(\d{2}) · (.+)", locator.inner_text())
    assert match, f"Missing stop text: {locator.inner_text()!r}"
    assert match.group(3).strip(), "Stop text has no reason."
    shown = int(match.group(1)) * 60 + int(match.group(2))
    now = datetime.now(timezone.utc)
    expected = (now.hour * 60 + now.minute + expected_minutes_from_now) % 1440
    distance = min((shown - expected) % 1440, (expected - shown) % 1440)
    assert distance <= 6, f"Stop time {match.group(1)}:{match.group(2)} is not about {expected_minutes_from_now} minutes from now."
    return match.group(3).strip()


def verify_time_control_round_trip(page, editor):
    time_control = editor.get_by_label("Time control", exact=True)
    expect(time_control).to_be_checked()
    start = editor.get_by_label("Start", exact=True)
    start_value = start.input_value()
    time_control.uncheck()
    for label in ["Start", "Stop", "Recurrence"]:
        expect(editor.get_by_label(label, exact=True)).to_have_count(0)
    expect(editor.get_by_role("button", name="Save template", exact=True)).to_be_visible()
    open_advanced(editor)
    expect(editor.get_by_label("Fade In (sec)", exact=True)).to_have_count(0)
    time_control.check()
    expect(editor.get_by_label("Start", exact=True)).to_have_value(start_value)
    expect(editor.get_by_role("button", name="Save schedule", exact=True)).to_be_visible()


def verify_editor_workflows(page, output_dir, browser):
    # Writes only to the runner's disposable, configured fixture.
    schedule_start, schedule_stop = inactive_schedule_times()
    page.set_viewport_size({"width": 390, "height": 844})
    page.goto(f"{BASE_URL}/automation?tab=schedules", wait_until="networkidle")
    page.get_by_role("tab", name="Date exceptions", exact=True).click()
    page.get_by_role("tab", name="Schedules", exact=True).click()
    expect(page.get_by_role("button", name="New schedule", exact=True)).to_be_visible()
    page.get_by_role("button", name="New schedule", exact=True).click()
    schedule = page.get_by_role("dialog", name="Schedule editor", exact=True)
    expect(schedule).to_be_visible()
    assert_editor_labels(schedule)
    page.wait_for_function("document.querySelector('.responsive-editor-dialog').contains(document.activeElement)")
    schedule.get_by_role("button", name="Close schedule editor").press("Shift+Tab")
    assert_focus_in_dialog(page, schedule)
    page.keyboard.press("Tab")
    assert_focus_in_dialog(page, schedule)
    schedule.get_by_label("Name", exact=True).fill("Browser smoke schedule")
    schedule.get_by_label("Volume for Test room", exact=True).fill("80")
    schedule.get_by_label("Selection", exact=True).select_option(label="Specific source")
    schedule.get_by_label("Source type", exact=True).select_option(label="Station")
    schedule.get_by_label("Source URL", exact=True).fill("http://127.0.0.1:1400/stream")
    schedule.get_by_label("Start", exact=True).fill(schedule_start)
    schedule.get_by_label("Stop", exact=True).fill(schedule_stop)
    schedule.get_by_role("checkbox", name="Include Test room in playback", exact=True).uncheck()
    command_count = SimulatedSpeakerHandler.command_count()
    schedule.get_by_role("button", name="Save schedule", exact=True).click()
    expect(schedule.get_by_text("Select at least one speaker.", exact=True)).to_be_visible()
    assert SimulatedSpeakerHandler.command_count() == command_count, "An empty playback target sent a Sonos command."
    schedule.get_by_role("checkbox", name="Include Test room in playback", exact=True).check()

    # Advanced groups priority, dates, fades and per-schedule exclusions; time control hides timing but keeps it.
    advanced = open_advanced(schedule)
    for label in ["Priority", "Start Date (optional)", "End Date (optional)", "Fade In (sec)", "Fade Out (sec)", "Excluded date"]:
        expect(advanced.get_by_label(label, exact=True)).to_be_visible()
    advanced.get_by_label("Fade In (sec)", exact=True).fill("5")
    advanced.get_by_label("Excluded date", exact=True).fill("2026-12-25")
    advanced.get_by_label("Repeat", exact=True).select_option("yearly")
    advanced.get_by_role("button", name="Add exclusion", exact=True).click()
    expect(advanced.get_by_text("25/12 · Every year", exact=True)).to_be_visible()
    verify_time_control_round_trip(page, schedule)
    expect(schedule.get_by_label("Fade In (sec)", exact=True)).to_have_value("5")
    expect(schedule.get_by_text("25/12 · Every year", exact=True)).to_be_visible()
    verify_editor_sizes(page, schedule, "Save schedule", advanced_labels=["Priority", "Fade Out (sec)", "Excluded date"])
    schedule.get_by_role("button", name="Save schedule", exact=True).click()
    expect(schedule.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    page.screenshot(path=str(output_dir / "schedule_mobile_editor_saved.png"))
    schedule.get_by_label("Name", exact=True).fill("Kept across resize")
    page.set_viewport_size({"width": 1280, "height": 900})
    expect(schedule).to_have_count(0)
    expect(page.get_by_label("Name", exact=True)).to_have_value("Kept across resize")
    page.get_by_role("button", name="Close schedule editor").click()
    confirmation = page.get_by_role("alertdialog", name="Unsaved changes")
    expect(confirmation).to_be_visible()
    page.wait_for_function("document.querySelector('[role=alertdialog]').contains(document.activeElement)")
    confirmation.get_by_role("button", name="Close dialog").press("Shift+Tab")
    assert_focus_in_dialog(page, confirmation)
    confirmation.get_by_role("button", name="Keep editing").click()
    expect(page.get_by_label("Name", exact=True)).to_have_value("Kept across resize")
    page.get_by_role("button", name="Close schedule editor").click()
    confirmation.get_by_role("button", name="Discard changes").click()

    page.set_viewport_size({"width": 390, "height": 844})
    page.get_by_role("button", name="Manual template", exact=True).click()
    template = page.get_by_role("dialog", name="Schedule editor", exact=True)
    expect(template.get_by_text("Manual template", exact=True).first).to_be_visible()
    expect(template.get_by_label("Time control", exact=True)).not_to_be_checked()
    template.get_by_label("Name", exact=True).fill("Browser smoke manual template")
    template.get_by_label("Volume for Test room", exact=True).fill("32")
    template.get_by_role("button", name="Save template", exact=True).click()
    expect(template.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    template.get_by_label("Time control", exact=True).check()
    expect(template.get_by_role("button", name="Save schedule", exact=True)).to_be_visible()
    template.get_by_label("Time control", exact=True).uncheck()
    template.get_by_role("button", name="Close schedule editor").click()
    expect(page.get_by_role("alertdialog", name="Unsaved changes")).to_have_count(0)

    # An existing schedule becomes a manual template and back without losing its timing.
    page.locator(".schedule-window-card__main").filter(has_text="Browser smoke schedule").click()
    round_trip = page.get_by_role("dialog", name="Schedule editor", exact=True)
    round_trip.get_by_label("Time control", exact=True).uncheck()
    round_trip.get_by_role("button", name="Save template", exact=True).click()
    expect(round_trip.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    round_trip.get_by_role("button", name="Close schedule editor").click()
    card = page.locator(".schedule-window-card").filter(has_text="Browser smoke schedule")
    expect(card.get_by_text("Manual playback template")).to_be_visible()
    expect(card.get_by_role("button", name="Start now Browser smoke schedule", exact=True)).to_be_visible()
    card.locator(".schedule-window-card__main").click()
    round_trip = page.get_by_role("dialog", name="Schedule editor", exact=True)
    round_trip.get_by_label("Time control", exact=True).check()
    expect(round_trip.get_by_label("Start", exact=True)).to_have_value(schedule_start)
    expect(round_trip.get_by_label("Stop", exact=True)).to_have_value(schedule_stop)
    round_trip.get_by_role("button", name="Save schedule", exact=True).click()
    expect(round_trip.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    round_trip.get_by_role("button", name="Close schedule editor").click()
    expect(page.locator(".schedule-window-card").filter(has_text="Browser smoke schedule").get_by_text(f"{schedule_start}-{schedule_stop}")).to_be_visible()

    # Keep the real source-editor validation and save path covered.
    page.goto(f"{BASE_URL}/library", wait_until="networkidle")
    page.get_by_role("button", name="Add source", exact=True).click()
    source = page.get_by_role("dialog", name="Add source", exact=True)
    source.get_by_role("button", name="Save source", exact=True).click()
    expect(source.locator("#source-name")).to_have_attribute("aria-invalid", "true")
    page.wait_for_function("document.activeElement.id === 'source-name'")
    source.get_by_label("Name", exact=True).fill("Browser smoke source")
    source.get_by_label("Source type", exact=True).select_option("Spotify")
    source.get_by_label("URL or service link", exact=True).fill("https://example.invalid/not-spotify")
    source.get_by_role("button", name="Save source", exact=True).click()
    expect(source.locator("#source-url-error")).to_be_visible()
    page.wait_for_function("document.activeElement.id === 'source-url'")
    source.get_by_label("URL or service link", exact=True).fill("spotify:playlist:abc123")
    source.get_by_role("button", name="Save source", exact=True).click()
    expect(source).to_have_count(0)
    expect(page.get_by_role("heading", name="Browser smoke source", exact=True)).to_be_visible()

    # Device volumes: bounded typing, reload persistence, manual form and removal dialog focus.
    page.goto(f"{BASE_URL}/administration/devices", wait_until="networkidle")
    card = page.locator(".device-card").first
    startup = page.get_by_label("Startup volume for Test room", exact=True)
    verify_volume_input(startup)
    startup.press("Tab")
    page.reload(wait_until="networkidle")
    startup = page.get_by_label("Startup volume for Test room", exact=True)
    expect(startup).to_have_value("100")
    manual_volume = page.locator("#manual-speaker-volume")
    verify_volume_input(manual_volume)
    manual_volume.fill("")
    player_top = page.locator(".global-player-bar").evaluate("element => element.getBoundingClientRect().top")
    assert card.evaluate("element => element.getBoundingClientRect().top") < player_top
    remove = page.get_by_role("button", name="Remove Test room", exact=True)
    remove.click()
    device = page.get_by_role("alertdialog", name="Remove speaker?")
    expect(device).to_be_visible()
    page.wait_for_function("document.querySelector('[role=alertdialog]').contains(document.activeElement)")
    device.get_by_role("button", name="Close dialog").press("Shift+Tab")
    assert_focus_in_dialog(page, device)
    device.get_by_role("button", name="Cancel", exact=True).press("Escape")
    expect(device).to_have_count(0)
    page.wait_for_function("document.activeElement.getAttribute('aria-label') === 'Remove Test room'")

    # Per-speaker stop rules enforce a positive manual limit and persist across reload. The cutoff has
    # already passed so the next manual start is limited by the manual minutes.
    stop_time, release_time = stop_rule_times()
    room_rules = page.locator(".device-card").filter(has_text="Test room").locator(".manual-stop-settings")
    room_rules.locator("summary").click()
    room_rules.get_by_label("Manual playback limit (minutes)").fill("")
    monday = room_rules.locator(".manual-stop-day").filter(has=page.get_by_text("Monday", exact=True))
    monday.get_by_role("checkbox").check()
    room_rules.get_by_role("button", name="Save stop rules").click()
    expect(room_rules.get_by_role("status").filter(has_text="positive manual playback limit")).to_be_visible()
    room_rules.get_by_label("Manual playback limit (minutes)").fill("45")
    monday.locator("input[type='time']").nth(0).fill(stop_time)
    monday.locator("input[type='time']").nth(1).fill(release_time)
    monday.get_by_role("button", name="Copy to all days").click()
    room_rules.get_by_role("button", name="Save stop rules").click()
    expect(room_rules.get_by_role("status").filter(has_text="Daily stop rules saved")).to_be_visible()
    page.reload(wait_until="networkidle")
    room_rules = page.locator(".device-card").filter(has_text="Test room").locator(".manual-stop-settings")
    room_rules.locator("summary").click()
    expect(room_rules.get_by_label("Manual playback limit (minutes)")).to_have_value("45")
    monday = room_rules.locator(".manual-stop-day").filter(has=page.get_by_text("Monday", exact=True))
    expect(monday.get_by_role("checkbox")).to_be_checked()
    expect(monday.locator("input[type='time']").nth(0)).to_have_value(stop_time)
    assert room_rules.locator(".manual-stop-day").count() == 7

    # Legacy scenes are converted to manual templates by settings migration.
    page.goto(f"{BASE_URL}/automation?tab=schedules", wait_until="networkidle")
    legacy = page.locator(".schedule-window-card__main").filter(has_text="Legacy smoke scene")
    expect(legacy).to_be_visible()
    legacy.click()
    legacy_editor = page.get_by_role("dialog", name="Schedule editor", exact=True)
    expect(legacy_editor.get_by_label("Include Test room in playback")).to_be_checked()
    legacy_editor.get_by_role("button", name="Save template", exact=True).click()
    expect(legacy_editor.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    legacy_editor.get_by_role("button", name="Close schedule editor").click()

    # Maximum volume: bounded typing, blur commit, reload persistence, then a lower cap clamps an embedded draft.
    page.goto(f"{BASE_URL}/administration/settings", wait_until="networkidle")
    verify_volume_input(page.locator("#maxVolume"))
    page.locator("#maxVolume").press("Tab")
    page.reload(wait_until="networkidle")
    expect(page.locator("#maxVolume")).to_have_value("100")
    save_status = page.get_by_role("status", name="Configuration save status")
    page.locator("#maxVolume").fill("40")
    expect(page.locator("#maxVolume")).to_have_value("40")
    expect(save_status).to_have_text("Saved")
    page.reload(wait_until="networkidle")
    expect(page.locator("#maxVolume")).to_have_value("40")
    page.goto(f"{BASE_URL}/automation?tab=schedules", wait_until="networkidle")
    page.locator(".schedule-window-card__main").filter(has_text="Browser smoke schedule").click()
    cap_editor = page.get_by_role("dialog", name="Schedule editor", exact=True)
    capped_volume = cap_editor.get_by_label("Volume for Test room", exact=True)
    capped_volume.fill("80")
    expect(capped_volume).to_have_value("40")
    cap_editor.get_by_role("button", name="Save schedule", exact=True).click()
    expect(cap_editor.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    cap_editor.get_by_role("button", name="Close schedule editor").click()
    page.reload(wait_until="networkidle")
    page.locator(".schedule-window-card__main").filter(has_text="Browser smoke schedule").click()
    cap_editor = page.get_by_role("dialog", name="Schedule editor", exact=True)
    expect(cap_editor.get_by_label("Volume for Test room", exact=True)).to_have_value("40")
    cap_editor.get_by_role("button", name="Save schedule", exact=True).click()
    expect(cap_editor.get_by_text("Schedule window saved.", exact=True)).to_be_visible()
    page.goto(f"{BASE_URL}/administration/settings", wait_until="networkidle")
    page.locator("#maxVolume").fill("100")
    expect(page.get_by_role("status", name="Configuration save status")).to_have_text("Saved")
    page.reload(wait_until="networkidle")
    expect(page.locator("#maxVolume")).to_have_value("100")

    # Browser back and reload both protect a dirty draft.
    confirmation = page.get_by_role("alertdialog", name="Unsaved changes")
    page.goto(f"{BASE_URL}/automation?tab=schedules", wait_until="networkidle")
    page.get_by_role("tab", name="Date exceptions", exact=True).click()
    page.get_by_role("tab", name="Schedules", exact=True).click()
    page.locator(".schedule-window-card__main").filter(has_text="Browser smoke schedule").click()
    page.get_by_label("Name", exact=True).fill("Back navigation draft")
    page.go_back(wait_until="domcontentloaded")
    expect(confirmation).to_be_visible()
    confirmation.get_by_role("button", name="Keep editing").click()
    expect(page.get_by_label("Name", exact=True)).to_have_value("Back navigation draft")
    leave_dialogs = []

    def cancel_reload(dialog):
        leave_dialogs.append(dialog.type)
        dialog.dismiss()

    page.on("dialog", cancel_reload)
    try:
        page.reload(wait_until="domcontentloaded", timeout=5000)
    except Error:
        pass  # A dismissed beforeunload dialog cancels the reload.
    finally:
        page.remove_listener("dialog", cancel_reload)
    assert leave_dialogs == ["beforeunload"], "Dirty draft did not protect reload."
    expect(page.get_by_label("Name", exact=True)).to_have_value("Back navigation draft")
    page.get_by_role("button", name="Close schedule editor").click()
    confirmation.get_by_role("button", name="Discard changes").click()

    # Manual start from the template: Home stays compact and shows the persisted stop with its reason.
    page.goto(f"{BASE_URL}/automation?tab=schedules", wait_until="networkidle")
    page.get_by_role("button", name="Start now Browser smoke manual template", exact=True).click()
    expect(page.get_by_text("Browser smoke manual template started.", exact=True)).to_be_visible()
    page.goto(f"{BASE_URL}/", wait_until="networkidle")
    expect(page.locator(".np-hero__playback-state")).to_have_text("Playing", timeout=10000)
    hero = page.locator("[data-qa='now-playing-hero']")
    assert hero.evaluate("element => element.getBoundingClientRect().height") <= 400
    assert page.locator("#home-rooms-heading").evaluate("element => element.getBoundingClientRect().bottom") < page.locator(".global-player-bar").evaluate("element => element.getBoundingClientRect().top")
    volume_field = page.locator("#home-now-playing-volume-number")
    verify_volume_input(volume_field)
    volume_field.fill("34")
    expect(hero.locator(".np-hero__record")).to_be_visible()
    expect(hero.locator(".np-hero__record.is-spinning")).to_be_visible()
    expect(hero.locator("img")).to_have_count(0)
    page.screenshot(path=str(output_dir / "playback_broken_cover_fallback.png"))
    room_stop = page.locator("[data-qa='room-card'] .room-card__auto-stop").first
    room_reason = assert_stops_at(room_stop, 45)
    player_stop = page.locator(".global-player-bar__stop-reason")
    player_reason = assert_stops_at(player_stop, 45)
    assert room_reason == player_reason == "Daily auto-stop", f"Unexpected stop reasons: {room_reason!r}, {player_reason!r}"
    print("Editor workflows passed: real saves, time control round trip, advanced section, labels, focus, draft protection, browser back/reload, resizing, 200% text and compact Home.")
    print("Editor and playback checks passed: empty targets, legacy scenes, volume caps, bounded typing, nullable volumes, broken-cover fallback and stop-rule 'Stops at' on room card and player.")


def verify_volume_input(field):
    for raw, expected in [("1008", "100"), ("456051", "100"), ("-12", "0"), ("1e4", "100")]:
        field.fill(raw)
        expect(field).to_have_value(expected)
    # Appending to an already clamped value previously left the raw DOM value behind.
    field.press("End")
    field.press("8")
    expect(field).to_have_value("100")


def verify_external_snapshot_updates(page):
    page.goto(f"{BASE_URL}/", wait_until="networkidle")
    SimulatedSpeakerHandler.set_external_state(playing=True, volume=63)
    hero_state = page.locator(".np-hero__playback-state")
    expect(hero_state).to_have_text("Playing", timeout=10000)
    expect(page.locator("[data-qa='home-player-toggle']")).to_have_attribute("aria-label", "Pause playback", timeout=10000)
    expect(page.locator("#home-now-playing-volume-number")).to_have_value("63", timeout=10000)
    expect(page.locator("#global-player-volume-number")).to_have_value("63", timeout=10000)
    expect(page.locator(".room-card--active input[type='number']")).to_have_value("63", timeout=10000)

    SimulatedSpeakerHandler.set_external_state(playing=False, volume=22)
    expect(hero_state).to_have_text("Paused", timeout=10000)
    expect(page.locator("[data-qa='home-player-toggle']")).to_have_attribute("aria-label", "Start playback", timeout=10000)
    expect(page.locator("#home-now-playing-volume-number")).to_have_value("22", timeout=10000)
    expect(page.locator("#global-player-volume-number")).to_have_value("22", timeout=10000)
    expect(page.locator(".room-card--active input[type='number']")).to_have_value("22", timeout=10000)
    SimulatedSpeakerHandler.set_external_state(playing=False, volume=34)


def verify_sleep_timer_workflow(page, browser, output_dir):
    page.get_by_role("button", name="Open expanded player").click()
    sheet = page.get_by_role("dialog", name="Now playing")
    timer_status = sheet.locator(".player-sheet__timer [role='status']")
    sheet.locator("#player-timer-minutes").fill("1")
    sheet.get_by_role("button", name="Start", exact=True).click()
    expect(timer_status).to_contain_text("pauses at")
    page.reload(wait_until="networkidle")
    page.get_by_role("button", name="Open expanded player").click()
    sheet = page.get_by_role("dialog", name="Now playing")
    expect(sheet.locator(".player-sheet__timer [role='status']")).to_contain_text("pauses at")

    secondary_context = browser.new_context(viewport={"width": 390, "height": 844})
    try:
        secondary = secondary_context.new_page()
        succeeded = False
        login_errors = []
        for username, password in get_login_attempts():
            succeeded, current_url, error_text = try_login(secondary, username, password)
            if succeeded:
                break
            login_errors.append(f"{username}@{current_url} ({error_text or 'no error message'})")
        assert succeeded, "Second browser could not log in: " + "; ".join(login_errors)
        secondary.get_by_role("button", name="Open expanded player").click()
        secondary_sheet = secondary.get_by_role("dialog", name="Now playing")
        secondary_timer = secondary_sheet.locator(".player-sheet__timer [role='status']")
        expect(secondary_timer).to_contain_text("pauses at")
        secondary_timer_parent = secondary.locator(".player-sheet__timer")
        secondary_timer_parent.get_by_role("button", name="Cancel", exact=True).click()
        expect(secondary_timer).to_have_text("Not running")
        expect(sheet.locator(".player-sheet__timer [role='status']")).to_have_text("Not running")

        sheet.locator("#player-timer-minutes").fill("1")
        sheet.get_by_role("button", name="Start", exact=True).click()
        expect(timer_status).to_contain_text("pauses at")
        secondary_timer_parent.locator("#player-timer-minutes").fill("3")
        secondary_timer_parent.get_by_role("button", name="Start", exact=True).click()
        expect(timer_status).to_contain_text("pauses at")
        replacement_started = time.monotonic()

        page.goto(f"{BASE_URL}/")
        page.locator("[data-qa='home-player-toggle']").click()
        expect(page.locator(".np-hero__playback-state")).to_have_text("Playing", timeout=10000)
        hero = page.locator("[data-qa='now-playing-hero']")
        expect(hero.locator(".np-hero__record.is-spinning")).to_be_visible(timeout=10000)
        expect(hero.locator("img")).to_have_count(0, timeout=10000)
        page.screenshot(path=str(output_dir / "playback_broken_cover_fallback.png"))
        page.get_by_role("button", name="Open expanded player").click()
        expect(page.locator(".player-sheet__timer [role='status']")).to_contain_text("pauses at")
        print("Waiting past the original one-minute deadline to verify its replacement remains armed.")
        while time.monotonic() - replacement_started < 65:
            page.wait_for_timeout(1000)
        expect(page.locator(".np-hero__playback-state")).to_have_text("Playing", timeout=10000)
        expect(page.locator(".player-sheet__timer [role='status']")).to_contain_text("pauses at")
        print("Replacement sleep timer remained armed beyond the original one-minute deadline.")
        secondary_timer_parent.get_by_role("button", name="Cancel", exact=True).click()
        expect(secondary_timer).to_have_text("Not running")
        expect(page.locator(".player-sheet__timer [role='status']")).to_have_text("Not running")
        return secondary_context
    except Exception:
        secondary_context.close()
        raise


def verify_application(playwright, output_dir, expected_speaker_state=None):
    browser = launch_chromium(playwright)
    context = browser.new_context(viewport={"width": 390, "height": 844})
    try:
        page = context.new_page()
        browser_errors = []
        page.on("console", lambda message: browser_errors.append(f"console: {message.text}") if message.type == "error" else None)
        page.on("pageerror", lambda error: browser_errors.append(f"pageerror: {error}"))
        login_attempt_errors = []
        for username, password in get_login_attempts():
            succeeded, current_url, error_text = try_login(page, username, password)
            if succeeded:
                break
            login_attempt_errors.append(f"{username}@{current_url} ({error_text or 'no error message'})")
        else:
            page.screenshot(path=str(output_dir / "mobile_login_failure.png"), full_page=True)
            raise AssertionError("Unable to log in. Set MOBILE_SMOKE_USERNAME and MOBILE_SMOKE_PASSWORD. " + "; ".join(login_attempt_errors))

        expect(page.locator("[data-qa='home-dashboard']")).to_be_visible(timeout=10000)
        with_speaker = page.locator("[data-qa='home-setup']").count() == 0
        if expected_speaker_state is not None:
            assert with_speaker == expected_speaker_state, "Isolated speaker fixture did not match the expected configuration."
        secondary_context = None
        if expected_speaker_state is True:
            SimulatedSpeakerHandler.reset()
            verify_external_snapshot_updates(page)
            secondary_context = verify_sleep_timer_workflow(page, browser, output_dir)
        verify_responsive_home(page, output_dir, with_speaker)
        for viewport_slug, width, height in VIEWPORTS:
            page.set_viewport_size({"width": width, "height": height})
            for theme in THEMES:
                for route, slug, expected_text in ROUTES:
                    page.goto(f"{BASE_URL}{route}", wait_until="networkidle")
                    apply_theme(page, theme)
                    if route == "/" and not with_speaker:
                        assert_home_onboarding(page)
                    else:
                        expect(page.locator("article.content").get_by_text(expected_text, exact=True).first).to_be_visible(timeout=10000)
                    assert_player_state(page, with_speaker)
                    if width < 992:
                        verify_drawer(page)
                    assert_no_horizontal_overflow(page)
                    if route == "/library":
                        assert_library_cards_are_uniform(page)
                    if route == "/automation":
                        assert page.locator(".schedule-editor-panel").evaluate(
                            "element => parseFloat(getComputedStyle(element).borderTopWidth) > 0"
                        ), "Reusable editor lost its panel styling."
                    suffix = "" if with_speaker else "_setup"
                    page.screenshot(path=str(output_dir / f"{slug}_{viewport_slug}_{theme}{suffix}.png"), full_page=True)
        if expected_speaker_state is True:
            verify_editor_workflows(page, output_dir, browser)
            expect(page.locator(".np-hero__playback-state")).to_have_text("Playing", timeout=10000)
            page.get_by_role("button", name="Open expanded player").click()
            timer_panel = page.locator(".player-sheet__timer")
            timer_panel.locator("#player-timer-minutes").fill("1")
            timer_panel.get_by_role("button", name="Start", exact=True).click()
            timer_status = timer_panel.locator("[role='status']")
            expect(timer_status).to_contain_text("pauses at")
            player_toggle = page.locator(".player-sheet__play")
            player_toggle.click()
            expect(page.locator(".np-hero__playback-state")).to_have_text("Paused", timeout=10000)
            player_toggle.click()
            expect(page.locator(".np-hero__playback-state")).to_have_text("Playing", timeout=10000)
            expect(timer_status).to_contain_text("pauses at")
            secondary_timer_status = None
            if secondary_context is not None:
                secondary_timer_status = secondary_context.pages[0].locator(".player-sheet__timer [role='status']")
                expect(secondary_timer_status).to_contain_text("pauses at")
            print("Fresh one-minute sleep timer survived manual playback; waiting for its deadline.")
            page.reload(wait_until="networkidle")
            page.get_by_role("button", name="Open expanded player").click()
            timer_status = page.locator(".player-sheet__timer [role='status']")
            expect(timer_status).to_contain_text("pauses at")
            if secondary_timer_status is not None:
                expect(secondary_timer_status).to_contain_text("pauses at")
            expect(page.locator(".np-hero__playback-state")).to_have_text("Paused", timeout=75000)
            expect(page.locator(".player-sheet__timer [role='status']")).to_have_text("Not running", timeout=10000)
            if secondary_timer_status is not None:
                expect(secondary_timer_status).to_have_text("Not running", timeout=10000)
            print("Shared snapshot checks passed across Home, dock and room cards; shared sleep timer survived reload, cross-browser cancellation/replacement, manual playback, and expiry.")
        if secondary_context is not None:
            secondary_context.close()
        assert not browser_errors, "Browser errors detected:\n" + "\n".join(browser_errors)
        print(f"UI smoke passed: {len(VIEWPORTS)} viewports × {len(THEMES)} themes × {len(ROUTES)} routes; configured speaker={with_speaker}.")
    finally:
        if 'secondary_context' in locals() and secondary_context is not None:
            secondary_context.close()
        context.close()
        browser.close()


def run():
    output_dir = Path("mobile_smoke_screenshots")
    output_dir.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        if is_server_reachable():
            verify_application(playwright, output_dir)
            return
        if not AUTO_START_SERVER:
            raise RuntimeError(f"App is not reachable at {BASE_URL}. Start the app or enable auto-start.")
        for with_speaker in [False, True]:
            speaker = None
            process = log_stream = runtime_dir = None
            try:
                if with_speaker:
                    SimulatedSpeakerHandler.reset()
                    speaker = ThreadingHTTPServer(("127.0.0.1", 1400), SimulatedSpeakerHandler)
                    threading.Thread(target=speaker.serve_forever, daemon=True).start()
                process, log_stream, log_path, runtime_dir = start_local_server(with_speaker)
                if not wait_for_server_ready(SERVER_START_TIMEOUT_SECONDS, process):
                    raise RuntimeError(f"Timed out waiting for {BASE_URL} (exit code: {process.poll()}). Check {log_path}")
                verify_application(playwright, output_dir, with_speaker)
            finally:
                stop_local_server(process, log_stream, runtime_dir)
                if speaker is not None:
                    speaker.shutdown()
                    speaker.server_close()


if __name__ == "__main__":
    run()
