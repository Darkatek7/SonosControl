import os
import platform
import re
import sqlite3
import subprocess
import time
from pathlib import Path
from urllib.error import URLError, HTTPError
from urllib.request import urlopen

from playwright.sync_api import sync_playwright, expect


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

ROUTES = [
    ("/", "home", "Favourites"),
    ("/library", "library", "Library"),
    ("/automation", "automation", "Automation"),
    ("/insights", "insights", "Insights"),
    ("/administration/devices", "devices", "Devices"),
    ("/administration/settings", "settings", "System settings"),
    ("/administration/users", "users", "Accounts"),
    ("/administration/backups", "backups", "Backups"),
]


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


def start_local_server():
    artifacts_dir = Path("artifacts")
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    log_path = artifacts_dir / "mobile_smoke_server.log"
    log_stream = log_path.open("w", encoding="utf-8")

    process = subprocess.Popen(
        ["dotnet", "run", "--project", "SonosControl.Web", "--no-build", "--urls", BASE_URL],
        stdout=log_stream,
        stderr=subprocess.STDOUT,
        cwd=Path(__file__).resolve().parent,
        shell=False,
        env={
            **os.environ,
            "BackgroundServices__Enabled": os.getenv("BackgroundServices__Enabled", "false"),
            "DataProtection__KeysDirectory": os.getenv(
                "DataProtection__KeysDirectory",
                str((Path(__file__).resolve().parent / "artifacts" / "mobile_smoke_dataprotection_keys").resolve()),
            ),
        },
    )
    return process, log_stream, log_path


def stop_local_server(process, log_stream):
    if process and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
    if log_stream:
        log_stream.close()


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
                bottomGap: Math.abs(window.innerHeight - playerRect.bottom),
                paddingBottom,
                playerHeight: playerRect.height,
                position: playerStyle.position
            };
        }
        """
    )
    assert spacing is not None, "Global bottom player or content region missing."
    assert spacing["bottomGap"] <= 2, "Global player is not anchored to the bottom viewport edge."
    if spacing["position"] == "fixed":
        assert (
            spacing["paddingBottom"] + 4 >= spacing["playerHeight"]
        ), "Main content does not reserve enough space for the global bottom player."


def assert_home_dashboard_layout(page):
    expect(page.locator("[data-qa='home-dashboard']")).to_be_visible(timeout=10000)
    expect(page.get_by_role("heading", name="Favourites")).to_be_visible(timeout=10000)
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


def verify_responsive_home(page, output_dir):
    for slug, width, height in VIEWPORTS:
        page.set_viewport_size({"width": width, "height": height})
        page.goto(f"{BASE_URL}/", wait_until="networkidle")
        assert_global_player_visible(page)
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
        page.screenshot(path=str(output_dir / f"home_{slug}.png"), full_page=True)


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
    page.wait_for_timeout(150)


def get_unlocked_admin_usernames():
    db_path = Path(__file__).resolve().parent / "SonosControl.Web" / "app.db"
    if not db_path.exists():
        return []

    try:
        with sqlite3.connect(db_path) as con:
            cur = con.cursor()
            rows = cur.execute(
                """
                SELECT DISTINCT u.UserName
                FROM AspNetUsers u
                JOIN AspNetUserRoles ur ON ur.UserId = u.Id
                JOIN AspNetRoles r ON r.Id = ur.RoleId
                WHERE r.Name IN ('admin', 'superadmin', 'operator')
                  AND u.LockoutEnd IS NULL
                ORDER BY CASE WHEN lower(u.UserName) = 'admin' THEN 0 ELSE 1 END, u.UserName
                """
            ).fetchall()
            return [row[0] for row in rows if row and row[0]]
    except sqlite3.Error:
        return []


def get_login_attempts():
    attempts = []
    seen = set()

    usernames = []
    passwords = []

    if USERNAME:
        usernames.append(USERNAME)
    if ADMIN_USERNAME:
        usernames.append(ADMIN_USERNAME)
    usernames.extend(get_unlocked_admin_usernames())
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


def run():
    server_process = None
    server_log_stream = None
    server_log_path = None
    started_local_server = False

    if AUTO_START_SERVER and not is_server_reachable():
        server_process, server_log_stream, server_log_path = start_local_server()
        started_local_server = True
        if not wait_for_server_ready(SERVER_START_TIMEOUT_SECONDS, process=server_process):
            exit_code = server_process.poll()
            stop_local_server(server_process, server_log_stream)
            raise RuntimeError(
                f"Timed out waiting for {BASE_URL} (server exit code: {exit_code}). "
                f"Check server log: {server_log_path}"
            )

    if not is_server_reachable():
        raise RuntimeError(
            f"App is not reachable at {BASE_URL}. Start the app or enable auto-start."
        )

    with sync_playwright() as p:
        try:
            browser = launch_chromium(p)
            context = browser.new_context(viewport={"width": 390, "height": 844})
            page = context.new_page()

            output_dir = Path("mobile_smoke_screenshots")
            output_dir.mkdir(parents=True, exist_ok=True)

            login_attempt_errors = []
            login_succeeded = False
            for username, password in get_login_attempts():
                login_succeeded, current_url, error_text = try_login(page, username, password)
                if login_succeeded:
                    break

                login_attempt_errors.append(
                    f"{username}@{current_url} ({error_text or 'no error message'})"
                )

            if not login_succeeded:
                page.screenshot(path=str(output_dir / "mobile_login_failure.png"), full_page=True)
                attempts_description = "; ".join(login_attempt_errors) or "none"
                raise AssertionError(
                    "Unable to log in. Set MOBILE_SMOKE_USERNAME and MOBILE_SMOKE_PASSWORD. "
                    f"Attempt results: {attempts_description}"
                )

            verify_responsive_home(page, output_dir)

            for viewport_slug, width, height in VIEWPORTS:
                page.set_viewport_size({"width": width, "height": height})
                for route, slug, expected_text in ROUTES:
                    page.goto(f"{BASE_URL}{route}", wait_until="networkidle")
                    main_content = page.locator("article.content")
                    expect(main_content.get_by_text(expected_text, exact=True).first).to_be_visible(timeout=10000)

                    assert_global_player_visible(page)
                    if width < 992:
                        verify_drawer(page)
                    assert_no_horizontal_overflow(page)
                    assert_bottom_player_does_not_cover_content(page)
                    if route == "/library":
                        assert_library_cards_are_uniform(page)

                    page.screenshot(path=str(output_dir / f"{slug}_{viewport_slug}.png"), full_page=True)

            context.close()
            browser.close()
        finally:
            if started_local_server:
                stop_local_server(server_process, server_log_stream)


if __name__ == "__main__":
    run()
