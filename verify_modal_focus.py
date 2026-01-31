from playwright.sync_api import sync_playwright, expect

def run():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()

        try:
            # Navigate to login
            print("Navigating to login...")
            page.goto("http://localhost:5107/auth/login")

            # Login
            print("Logging in...")
            page.fill("input[name='username']", "admin")
            page.fill("input[name='password']", "Test1234.")
            page.click("#loginBtn")

            # Wait for dashboard
            print("Waiting for dashboard...")
            expect(page.get_by_role("heading", name="Sonos Control Panel")).to_be_visible(timeout=10000)

            # Wait a bit for Blazor to be fully interactive
            page.wait_for_timeout(2000)

            # Find the button
            button = page.get_by_label("Add Station")
            print(f"Button visible: {button.is_visible()}")
            print(f"Button enabled: {button.is_enabled()}")

            # Click "Add" button for Stations
            print("Clicking Add Station...")
            button.click()

            # Wait a bit
            page.wait_for_timeout(2000)

            # Look for the modal content specifically
            print("Looking for 'Add New Station' text...")
            content = page.get_by_text("Add New Station")
            print(f"Content count: {content.count()}")
            if content.count() > 0:
                 print(f"Content visible: {content.first.is_visible()}")

            # Check for input
            input_el = page.locator("#mediaName")
            print(f"Input count: {input_el.count()}")

            if input_el.count() > 0:
                print(f"Input visible: {input_el.first.is_visible()}")
                 # Check focus
                focused_element = page.evaluate("document.activeElement.id")
                print(f"Focused element ID: {focused_element}")
                assert focused_element == "mediaName", f"Expected focus on 'mediaName', but got '{focused_element}'"
            else:
                print("Input #mediaName not found in DOM.")
                # Dump HTML
                with open("page_dump.html", "w") as f:
                    f.write(page.content())
                print("Dumped HTML to page_dump.html")

            # Take screenshot
            page.screenshot(path="verification_modal.png")
            print("Screenshot saved to verification_modal.png")

        except Exception as e:
            print(f"Error: {e}")
            page.screenshot(path="error_state.png")
            raise
        finally:
            browser.close()

if __name__ == "__main__":
    run()
