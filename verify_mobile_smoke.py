import os
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

ROUTES = [
    ("/", "home", "Sonos Control Panel"),
    ("/admin/users", "users", "User Management"),
    ("/config", "config", "System Configuration"),
    ("/logs", "logs", "System Logs"),
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
    has_overflow = page.evaluate(
        "document.documentElement.scrollWidth > window.innerWidth"
    )
    assert not has_overflow, "Page has horizontal overflow on mobile viewport."


def verify_drawer(page):
    menu_button = page.locator("button.app-mobile-menu-button")
    if menu_button.count() == 0:
        return

    menu_button.first.click()
    expect(page.locator("aside.app-sidebar.is-open")).to_be_visible(timeout=5000)

    backdrop = page.locator("button.app-drawer-backdrop")
    if backdrop.count() > 0:
        viewport = page.viewport_size or {"width": 390, "height": 844}
        page.mouse.click(viewport["width"] - 8, 80)
    else:
        # Fallback close by toggling menu button again.
        menu_button.first.click()

    expect(page.locator("aside.app-sidebar.is-open")).to_have_count(0, timeout=5000)


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
            browser = p.chromium.launch(headless=True)
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

            for route, slug, expected_text in ROUTES:
                page.goto(f"{BASE_URL}{route}", wait_until="networkidle")
                main_content = page.locator("article.content")
                expect(main_content.get_by_text(expected_text).first).to_be_visible(timeout=10000)

                verify_drawer(page)
                assert_no_horizontal_overflow(page)

                page.screenshot(path=str(output_dir / f"mobile_{slug}.png"), full_page=True)

            context.close()
            browser.close()
        finally:
            if started_local_server:
                stop_local_server(server_process, server_log_stream)


if __name__ == "__main__":
    run()
