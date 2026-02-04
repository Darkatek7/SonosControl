from playwright.sync_api import sync_playwright, expect

def run():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page()

        # Login
        page.goto("http://localhost:5107/auth/login")
        page.fill("input[name='username']", "admin")
        page.fill("input[name='password']", "Test1234.")
        page.click("button#loginBtn")

        # Wait for navigation
        page.wait_for_url("http://localhost:5107/")

        # Go to User Management
        page.goto("http://localhost:5107/admin/users")

        # Wait for table to load
        page.wait_for_selector(".user-management-table")

        # Take screenshot of the desktop view
        page.screenshot(path="verification_user_management.png")

        # Verify button structure (check for icons which exist in my new code)
        # Note: In my code, desktop buttons also use icons now?
        # Let's check the code I wrote.
        # Yes: <span class="oi @(userRoles[user.Id].Contains("admin") ? "oi-minus" : "oi-plus")" aria-hidden="true"></span> <span>Admin</span>

        # Before it was just text: @(userRoles[user.Id].Contains("admin") ? "−Admin" : "+Admin")

        # So finding an element with class 'oi-plus' or 'oi-minus' inside a button confirms the new structure.
        # But wait, the admin user can't edit themselves. "Disabled" attribute check.

        page.screenshot(path="verification_user_management_desktop.png")

        # Emulate mobile to check mobile view
        page.set_viewport_size({"width": 375, "height": 667})
        page.reload()
        page.wait_for_selector(".card") # Mobile cards

        page.screenshot(path="verification_user_management_mobile.png")

        browser.close()

if __name__ == "__main__":
    run()
