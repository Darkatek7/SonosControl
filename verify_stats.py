from playwright.sync_api import sync_playwright, expect
import time

def run():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()

        # Login
        page.goto("http://localhost:5107/auth/login")
        page.fill("input[name='username']", "admin")
        page.fill("input[name='password']", "Test1234.")
        page.click("button[type='submit']")

        # Wait for navigation to complete (usually redirects to returnUrl or home)
        page.wait_for_url("http://localhost:5107/")

        # Navigate to Stats
        page.goto("http://localhost:5107/stats")

        # Wait for headers to appear
        expect(page.get_by_role("heading", name="Statistics")).to_be_visible()
        expect(page.get_by_text("Total Listening Time")).to_be_visible()
        expect(page.get_by_text("Station Master")).to_be_visible()

        # Take screenshot
        page.screenshot(path="verification_stats.png", full_page=True)

        browser.close()

if __name__ == "__main__":
    run()
