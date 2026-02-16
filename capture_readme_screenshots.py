import argparse
import os
import sqlite3
import subprocess
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright


ROUTES = [
    ("/", "home", "Sonos Control Panel"),
    ("/config", "config", "System Configuration"),
    ("/admin/users", "users", "User Management"),
    ("/logs", "logs", "System Logs"),
]


def parse_viewport(viewport: str) -> dict:
    parts = viewport.lower().split("x")
    if len(parts) != 2:
        raise ValueError(f"Invalid viewport '{viewport}'. Use WIDTHxHEIGHT, e.g. 1366x768.")

    width = int(parts[0].strip())
    height = int(parts[1].strip())
    if width < 320 or height < 320:
        raise ValueError(f"Viewport '{viewport}' is too small.")

    return {"width": width, "height": height}


def is_server_reachable(base_url: str) -> bool:
    try:
        with urlopen(f"{base_url.rstrip('/')}/auth/login", timeout=3):
            return True
    except (URLError, HTTPError, TimeoutError, OSError):
        return False


def wait_for_server_ready(base_url: str, timeout_seconds: int, process=None) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if process and process.poll() is not None:
            return False

        if is_server_reachable(base_url):
            return True

        time.sleep(1)

    return False


def start_local_server(base_url: str, project_root: Path):
    artifacts_dir = project_root / "artifacts"
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    log_path = artifacts_dir / "readme_screenshots_server.log"
    log_stream = log_path.open("w", encoding="utf-8")

    process = subprocess.Popen(
        ["dotnet", "run", "--project", "SonosControl.Web", "--no-build", "--urls", base_url],
        stdout=log_stream,
        stderr=subprocess.STDOUT,
        cwd=project_root,
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


def find_db_path(project_root: Path) -> Path | None:
    candidates = [
        project_root / "SonosControl.Web" / "app.db",
        project_root / "SonosControl.Web" / "Data" / "app.db",
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def get_unlocked_admin_usernames(project_root: Path) -> list[str]:
    db_path = find_db_path(project_root)
    if not db_path:
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


def get_login_attempts(project_root: Path, username_arg: str | None, password_arg: str | None) -> list[tuple[str, str]]:
    seen = set()
    attempts: list[tuple[str, str]] = []

    usernames = [
        username_arg,
        os.getenv("README_SCREENSHOT_USERNAME"),
        os.getenv("MOBILE_SMOKE_USERNAME"),
        os.getenv("ADMIN_USERNAME"),
    ]
    usernames.extend(get_unlocked_admin_usernames(project_root))
    usernames.append("admin")

    passwords = [
        password_arg,
        os.getenv("README_SCREENSHOT_PASSWORD"),
        os.getenv("MOBILE_SMOKE_PASSWORD"),
        os.getenv("ADMIN_PASSWORD"),
        "Test1234.",
    ]

    for username in usernames:
        for password in passwords:
            if not username or not password:
                continue
            key = (username, password)
            if key in seen:
                continue
            seen.add(key)
            attempts.append(key)
            if len(attempts) >= 10:
                return attempts

    return attempts


def try_login(page, base_url: str, username: str, password: str) -> tuple[bool, str, str]:
    page.goto(f"{base_url.rstrip('/')}/auth/login", wait_until="networkidle")
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


def ensure_expected_heading(page, expected_text: str):
    main_content = page.locator("article.content")
    expect(main_content.get_by_text(expected_text).first).to_be_visible(timeout=10000)


def force_light_theme(page):
    page.evaluate(
        """
        () => {
            if (window.sonosTheme && typeof window.sonosTheme.apply === "function") {
                window.sonosTheme.apply("light");
            }
            document.documentElement.removeAttribute("data-theme");
        }
        """
    )
    page.wait_for_timeout(150)


def capture_route(page, base_url: str, route: str, slug: str, expected_text: str, output_path: Path):
    page.goto(f"{base_url.rstrip('/')}{route}", wait_until="networkidle")
    ensure_expected_heading(page, expected_text)
    force_light_theme(page)
    page.keyboard.press("Escape")
    page.wait_for_timeout(150)
    page.screenshot(path=str(output_path), full_page=False)


def capture_viewport_set(page, base_url: str, viewport_label: str, viewport: dict, output_dir: Path):
    page.set_viewport_size(viewport)
    for route, slug, expected_text in ROUTES:
        output_path = output_dir / f"{viewport_label}-{slug}.png"
        capture_route(page, base_url, route, slug, expected_text, output_path)
        print(f"Captured {output_path}")


def parse_args():
    parser = argparse.ArgumentParser(
        description=(
            "Capture README screenshots for desktop and mobile routes. "
            "For best visual results, prepare representative demo data (stations/log entries/users) before running."
        )
    )
    parser.add_argument("--base-url", default=os.getenv("README_SCREENSHOT_BASE_URL", "http://localhost:5107"))
    parser.add_argument("--username", default=None)
    parser.add_argument("--password", default=None)
    parser.add_argument("--out", default="docs/assets/readme")
    parser.add_argument("--desktop-viewport", default="1366x768")
    parser.add_argument("--mobile-viewport", default="390x844")
    parser.add_argument("--server-timeout", type=int, default=180)
    parser.add_argument("--no-autostart", action="store_true")
    return parser.parse_args()


def run():
    args = parse_args()
    project_root = Path(__file__).resolve().parent
    output_dir = Path(args.out)
    if not output_dir.is_absolute():
        output_dir = project_root / output_dir
    output_dir.mkdir(parents=True, exist_ok=True)

    desktop_viewport = parse_viewport(args.desktop_viewport)
    mobile_viewport = parse_viewport(args.mobile_viewport)

    print("Preparing README screenshot capture.")
    print("Tip: Use populated demo data for marketing-ready screenshots.")
    print(f"Output directory: {output_dir}")

    server_process = None
    server_log_stream = None
    server_log_path = None
    started_local_server = False

    if not args.no_autostart and not is_server_reachable(args.base_url):
        server_process, server_log_stream, server_log_path = start_local_server(args.base_url, project_root)
        started_local_server = True
        if not wait_for_server_ready(args.base_url, args.server_timeout, process=server_process):
            exit_code = server_process.poll()
            stop_local_server(server_process, server_log_stream)
            raise RuntimeError(
                f"Timed out waiting for {args.base_url} (server exit code: {exit_code}). "
                f"Check server log: {server_log_path}"
            )

    if not is_server_reachable(args.base_url):
        raise RuntimeError(
            f"App is not reachable at {args.base_url}. "
            "Start the app manually or omit --no-autostart."
        )

    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        context = browser.new_context(viewport=desktop_viewport)
        page = context.new_page()

        login_attempt_errors = []
        login_succeeded = False
        for username, password in get_login_attempts(project_root, args.username, args.password):
            login_succeeded, current_url, error_text = try_login(page, args.base_url, username, password)
            if login_succeeded:
                print(f"Login succeeded with user '{username}'.")
                break

            login_attempt_errors.append(
                f"{username}@{current_url} ({error_text or 'no error message'})"
            )

        if not login_succeeded:
            failure_path = output_dir / "readme_login_failure.png"
            page.screenshot(path=str(failure_path), full_page=False)
            attempts_description = "; ".join(login_attempt_errors) or "none"
            browser.close()
            if started_local_server:
                stop_local_server(server_process, server_log_stream)
            raise RuntimeError(
                "Unable to log in. Provide credentials with --username/--password or set env vars. "
                f"Attempt results: {attempts_description}"
            )

        capture_viewport_set(page, args.base_url, "desktop", desktop_viewport, output_dir)
        capture_viewport_set(page, args.base_url, "mobile", mobile_viewport, output_dir)

        context.close()
        browser.close()

    if started_local_server:
        stop_local_server(server_process, server_log_stream)

    print("README screenshot capture complete.")


if __name__ == "__main__":
    run()
