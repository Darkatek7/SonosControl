import argparse
import os
import platform
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Optional
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

from playwright.sync_api import expect, sync_playwright


CHROME_PATH = os.getenv("PLAYWRIGHT_CHROME_PATH")

ROUTES = [
    ("/", "home", "Favourites"),
    ("/library", "library", "Library"),
    ("/automation", "automation", "Automation"),
    ("/insights", "insights", "Insights"),
    ("/administration/devices", "administration", "Devices"),
]
THEMES = ["light", "dark"]


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


def resolve_chrome_path() -> Optional[str]:
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


def start_local_server(base_url: str, project_root: Path):
    artifacts_dir = project_root / "artifacts"
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    log_path = artifacts_dir / "readme_screenshots_server.log"
    log_stream = log_path.open("w", encoding="utf-8")
    runtime_dir = Path(tempfile.mkdtemp(prefix="sonoscontrol-readme-", dir=artifacts_dir)).resolve()
    settings_dir = runtime_dir / "settings"
    settings_dir.mkdir(parents=True, exist_ok=True)

    process = subprocess.Popen(
        ["dotnet", "run", "--project", "SonosControl.Web", "--no-build", "--urls", base_url],
        stdout=log_stream,
        stderr=subprocess.STDOUT,
        cwd=project_root,
        shell=False,
        env={
            **os.environ,
            "BackgroundServices__Enabled": os.getenv("BackgroundServices__Enabled", "false"),
            "Settings__DataDirectory": str(settings_dir),
            "ConnectionStrings__DefaultConnection": f"Data Source={runtime_dir / 'app.db'}",
            "ADMIN_USERNAME": os.getenv("ADMIN_USERNAME", "admin"),
            "ADMIN_EMAIL": os.getenv("ADMIN_EMAIL", "admin@readme.invalid"),
            "ADMIN_PASSWORD": os.getenv("ADMIN_PASSWORD", "Test1234."),
            "DataProtection__KeysDirectory": os.getenv(
                "DataProtection__KeysDirectory",
                str((runtime_dir / "keys").resolve()),
            ),
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


def get_login_attempts(username_arg: Optional[str], password_arg: Optional[str]) -> list[tuple[str, str]]:
    seen = set()
    attempts: list[tuple[str, str]] = []

    usernames = [
        username_arg,
        os.getenv("README_SCREENSHOT_USERNAME"),
        os.getenv("MOBILE_SMOKE_USERNAME"),
        os.getenv("ADMIN_USERNAME"),
    ]
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


def apply_theme(page, theme: str):
    page.evaluate(
        """
        theme => {
            if (window.sonosTheme && typeof window.sonosTheme.apply === "function") {
                window.sonosTheme.apply(theme);
            }
            document.documentElement.setAttribute("data-theme", theme);
            document.documentElement.style.colorScheme = theme;
        }
        """,
        theme,
    )
    page.wait_for_timeout(200)


def capture_route(page, base_url: str, route: str, expected_text: str, theme: str, output_path: Path):
    page.goto(f"{base_url.rstrip('/')}{route}", wait_until="networkidle")
    ensure_expected_heading(page, expected_text)
    apply_theme(page, theme)
    page.keyboard.press("Escape")
    page.mouse.move(1, 1)
    page.wait_for_timeout(200)
    page.screenshot(path=str(output_path), full_page=False)


def capture_viewport_set(page, base_url: str, viewport_label: str, viewport: dict, output_dir: Path):
    page.set_viewport_size(viewport)
    for theme in THEMES:
        for route, slug, expected_text in ROUTES:
            output_path = output_dir / f"{viewport_label}-{theme}-{slug}.png"
            capture_route(page, base_url, route, expected_text, theme, output_path)
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
    parser.add_argument("--out", default="docs/assets/readme/images")
    parser.add_argument("--desktop-viewport", default="1280x900")
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
    runtime_dir = None
    started_local_server = False

    if not args.no_autostart and not is_server_reachable(args.base_url):
        server_process, server_log_stream, server_log_path, runtime_dir = start_local_server(args.base_url, project_root)
        started_local_server = True
        if not wait_for_server_ready(args.base_url, args.server_timeout, process=server_process):
            exit_code = server_process.poll()
            stop_local_server(server_process, server_log_stream, runtime_dir)
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
        browser = launch_chromium(playwright)
        context = browser.new_context(viewport=desktop_viewport)
        page = context.new_page()

        login_attempt_errors = []
        login_succeeded = False
        for username, password in get_login_attempts(args.username, args.password):
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
                stop_local_server(server_process, server_log_stream, runtime_dir)
            raise RuntimeError(
                "Unable to log in. Provide credentials with --username/--password or set env vars. "
                f"Attempt results: {attempts_description}"
            )

        capture_viewport_set(page, args.base_url, "desktop", desktop_viewport, output_dir)
        capture_viewport_set(page, args.base_url, "mobile", mobile_viewport, output_dir)

        context.close()
        browser.close()

    if started_local_server:
        stop_local_server(server_process, server_log_stream, runtime_dir)

    print("README screenshot capture complete.")


if __name__ == "__main__":
    run()
