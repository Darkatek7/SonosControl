from playwright.sync_api import Page, expect, sync_playwright
import time
import os

def test_speaker_config_and_control(page: Page):
    """
    Verifies that the user can add multiple speakers in the Config page
    and select them in the Index page.
    """
    print(f"Starting verification on {page.url}")

    # 1. Navigate to Home
    try:
        page.goto("http://localhost:5107/", timeout=10000)
    except Exception as e:
        print(f"Failed to load home page: {e}")
        # Try again just in case
        page.goto("http://localhost:5107/")

    page.wait_for_load_state("networkidle")
    time.sleep(2)

    # 2. Check if login is needed
    if "login" in page.url or page.locator("form[action='/auth/login']").is_visible():
        print("Login page detected. Logging in...")
        try:
            username = os.getenv("ADMIN_USERNAME", "admin")
            password = os.getenv("ADMIN_PASSWORD")
            if not password:
                raise ValueError("ADMIN_PASSWORD environment variable is not set")

            page.fill("#username", username)
            page.fill("#password", password)
            page.click("button[type='submit']")

            # Wait for navigation
            page.wait_for_url("http://localhost:5107/", timeout=10000)
            print("Login successful, redirected to Home.")
        except Exception as e:
            print(f"Login failed or timeout: {e}")
            page.screenshot(path="debug_login_fail.png")
            raise e
    else:
        print("Already logged in or on Home page.")

    # 3. Go to Config Page
    print("Navigating to Config page...")
    page.goto("http://localhost:5107/config")

    try:
        # Wait for "System Configuration" header
        expect(page.get_by_role("heading", name="System Configuration")).to_be_visible(timeout=10000)
    except Exception as e:
        print("Config page header not found.")
        page.screenshot(path="debug_config_load_fail.png")
        raise e

    # 4. Add Speakers
    print("Adding speakers...")

    # Check if speakers already exist (from previous runs) and clear them if needed?
    # For now, just add new ones or rely on distinct names.
    # Let's add unique speakers based on timestamp to avoid duplicates if DB persists?
    # Or just "Office Speaker" and "Kitchen Speaker" and assume we can find them.

    # Fill Office Speaker
    # The inputs are in a row. The last row is for adding new speakers.
    # It has placeholders.

    try:
        # Identify the "add new" inputs by placeholder
        name_input = page.get_by_placeholder("e.g. Living Room")
        ip_input = page.get_by_placeholder("e.g. 192.168.1.50")
        add_btn = page.get_by_role("button", name="Add Speaker")

        # Add Office Speaker
        if not page.get_by_text("Office Speaker").is_visible():
            name_input.fill("Office Speaker")
            ip_input.fill("192.168.1.101")
            add_btn.click()
            expect(page.get_by_text("Office Speaker")).to_be_visible()
            print("Added Office Speaker")
        else:
            print("Office Speaker already exists")

        # Add Kitchen Speaker
        if not page.get_by_text("Kitchen Speaker").is_visible():
            name_input.fill("Kitchen Speaker")
            ip_input.fill("192.168.1.102")
            add_btn.click()
            expect(page.get_by_text("Kitchen Speaker")).to_be_visible()
            print("Added Kitchen Speaker")
        else:
            print("Kitchen Speaker already exists")

    except Exception as e:
        print("Failed to add speakers.")
        page.screenshot(path="debug_add_speaker_fail.png")
        raise e

    # 5. Go to Index Page
    print("Navigating to Index page...")
    page.goto("http://localhost:5107/")
    page.wait_for_load_state("networkidle")

    try:
        # Verify Active Speaker Dropdown
        print("Verifying dropdown...")
        # The dropdown label is "Active Speaker"
        expect(page.get_by_text("Active Speaker")).to_be_visible()

        # Select Office Speaker
        # Find the select element. It has class 'form-select'.
        select = page.locator("select.form-select")
        select.select_option(label="Office Speaker")
        print("Selected Office Speaker")

        # Verify Sync Play button
        sync_btn = page.get_by_role("button", name="Sync Play")
        expect(sync_btn).to_be_visible()
        print("Sync Play button is visible")

        # Click it just to verify it's interactable
        sync_btn.click()
        print("Clicked Sync Play")

    except Exception as e:
        print("Index page verification failed.")
        page.screenshot(path="debug_index_fail.png")
        raise e

    print("Verification complete!")
    page.screenshot(path="verification_success.png")

if __name__ == "__main__":
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()
        try:
            test_speaker_config_and_control(page)
        finally:
            browser.close()
