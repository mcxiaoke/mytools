#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
Huawei HN8546X6N-20 ONT Reboot Script using Playwright
Requires: playwright (e.g. in C:\Home\Develop\venv)
"""

import os
import time
import argparse
from playwright.sync_api import sync_playwright

def reboot(host="192.168.3.1", username="user", password=None, dry_run=False, edge_path=r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"):
    password = password or os.environ.get("MODERM_PASS", "")
    if not password:
        raise ValueError("Password is required. Please set the MODERM_PASS environment variable or provide --password.")

    base_url = f"http://{host.replace('http://', '').replace('https://', '').rstrip('/')}"
    with sync_playwright() as p:
        browser = p.chromium.launch(executable_path=edge_path, headless=True)
        page = browser.new_page()

        print(f"Opening {base_url}...")
        page.goto(f"{base_url}/", timeout=15000)
        time.sleep(1)

        print("Logging in...")
        page.fill("#txt_Username", username)
        page.fill("#txt_Password", password)
        page.click("#btnSubmit")
        time.sleep(4)

        print("Navigating to Reboot management page...")
        page.goto(f"{base_url}/html/ssmp/devmanage/cmccdevicereset.asp", timeout=15000)
        time.sleep(2)

        token = page.evaluate('() => (document.getElementById("hwonttoken") || document.getElementsByName("onttoken")[0]).value')
        print(f"Acquired reboot onttoken: {token}")

        if dry_run:
            print("[DRY-RUN] Success. Browser closing without rebooting.")
        else:
            print("Triggering reboot...")
            # Automatically accept confirm dialog
            page.on("dialog", lambda dialog: dialog.accept())
            page.click("#Restart_button")
            time.sleep(2)
            print("Reboot signal sent!")

        browser.close()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Playwright router reboot script")
    parser.add_argument("--host", default="192.168.3.1", help="Router IP")
    parser.add_argument("--user", default="user", help="Username")
    parser.add_argument("--password", default=os.environ.get("MODERM_PASS", ""), help="Password (default: MODERM_PASS env var)")
    parser.add_argument("--dry-run", action="store_true", help="Dry run only")
    parser.add_argument("--reboot", action="store_true", help="Perform reboot")
    args = parser.parse_args()
    reboot(host=args.host, username=args.user, password=args.password, dry_run=not args.reboot)
