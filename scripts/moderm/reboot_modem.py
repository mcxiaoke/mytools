#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Huawei HN8546X6N-20 (CMCC) ONT Router Reboot Script
Supported on Python 3.8+ (Zero external dependencies, standard library urllib based)
"""

import os
import sys
import time
import base64
import re
import argparse
import logging
import urllib.request
import urllib.parse
import http.cookiejar

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger("modem_reboot")


class ModemController:
    def __init__(self, host="192.168.3.1", username="user", password=None, timeout=10):
        self.host = host.rstrip("/")
        if not self.host.startswith("http://") and not self.host.startswith("https://"):
            self.base_url = f"http://{self.host}"
        else:
            self.base_url = self.host
        self.username = username
        self.password = password or os.environ.get("MODERM_PASS", "")
        if not self.password:
            raise ValueError(
                "Password is required. Please set the MODERM_PASS environment variable or provide --password."
            )
        self.timeout = timeout

        self.cookie_jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.cookie_jar)
        )
        self.session_token = None

    def _request(self, path, data=None, headers=None, method=None):
        url = urllib.parse.urljoin(self.base_url, path)
        encoded_data = None
        if data is not None:
            if isinstance(data, dict):
                encoded_data = urllib.parse.urlencode(data).encode("utf-8")
            elif isinstance(data, str):
                encoded_data = data.encode("utf-8")
            else:
                encoded_data = data

        req_headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        }
        if data is not None:
            req_headers["Content-Type"] = "application/x-www-form-urlencoded"
        if headers:
            req_headers.update(headers)

        req = urllib.request.Request(url, data=encoded_data, headers=req_headers, method=method)
        with self.opener.open(req, timeout=self.timeout) as resp:
            content = resp.read().decode("utf-8", errors="ignore")
            return resp.status, content, resp.headers

    def get_rand_token(self):
        """Step 1: Obtain random security token from /asp/GetRandCount.asp"""
        status, text, _ = self._request("/asp/GetRandCount.asp", data=b"")
        token = text.strip().lstrip("\ufeff")
        if not token:
            raise RuntimeError("Failed to fetch random token from /asp/GetRandCount.asp")
        return token

    def login(self):
        """Step 2 & 3: Perform login to /login.cgi"""
        logger.info(f"Connecting to router at {self.base_url}...")
        rand_token = self.get_rand_token()
        logger.info(f"Obtained login challenge token: {rand_token[:10]}...")

        # Initialize Cookie header
        cookie = http.cookiejar.Cookie(
            version=0,
            name="Cookie",
            value="body:Language:chinese:id=-1",
            port=None,
            port_specified=False,
            domain=urllib.parse.urlparse(self.base_url).hostname,
            domain_specified=False,
            domain_initial_dot=False,
            path="/",
            path_specified=True,
            secure=False,
            expires=None,
            discard=True,
            comment=None,
            comment_url=None,
            rest={"HttpOnly": None},
            rfc2109=False
        )
        self.cookie_jar.set_cookie(cookie)

        pwd_b64 = base64.b64encode(self.password.encode("utf-8")).decode("utf-8")
        login_payload = {
            "UserName": self.username,
            "PassWord": pwd_b64,
            "x.X_HW_Token": rand_token
        }
        login_headers = {
            "Referer": f"{self.base_url}/",
            "Origin": self.base_url
        }

        status, text, resp_headers = self._request(
            "/login.cgi",
            data=login_payload,
            headers=login_headers
        )

        # Check cookies for CookieHttp session
        cookies_dict = {c.name: c.value for c in self.cookie_jar}
        if "CookieHttp" not in cookies_dict:
            raise RuntimeError(f"Login failed! 'CookieHttp' session cookie not found. Cookies: {cookies_dict}")

        logger.info("Login succeeded! Active session acquired.")
        return True

    def get_reboot_token(self):
        """Step 4: Fetch device reset page to extract dynamic onttoken"""
        status, html, _ = self._request(
            "/html/ssmp/devmanage/cmccdevicereset.asp",
            headers={"Referer": f"{self.base_url}/index.asp"}
        )
        match = re.search(r'id=["\']hwonttoken["\'][^>]*value=["\']([^"\']+)["\']', html) or \
                re.search(r'name=["\']onttoken["\'][^>]*value=["\']([^"\']+)["\']', html)
        if not match:
            raise RuntimeError("Failed to extract onttoken from cmccdevicereset.asp page!")
        
        token = match.group(1).strip()
        logger.info(f"Retrieved reboot onttoken: {token[:10]}...")
        return token

    def reboot(self):
        """Step 5: Send reboot command to set.cgi"""
        onttoken = self.get_reboot_token()
        reboot_url = (
            "/html/ssmp/devmanage/set.cgi?"
            "x=InternetGatewayDevice.X_HW_DEBUG.SMP.DM.ResetBoard&"
            "RequestFile=html/ssmp/devmanage/cmccdevicereset.asp"
        )
        payload = {
            "x.X_HW_Token": onttoken
        }
        headers = {
            "Referer": f"{self.base_url}/html/ssmp/devmanage/cmccdevicereset.asp",
            "Origin": self.base_url
        }

        logger.info("Sending REBOOT command to router...")
        try:
            status, _, _ = self._request(reboot_url, data=payload, headers=headers)
            logger.info(f"Reboot request dispatched successfully (HTTP Status {status})! The router is now rebooting.")
            return True
        except Exception as e:
            # During reboot, connection may drop immediately which is expected
            logger.info(f"Reboot request triggered (connection dropped/reset: {e}). Router is rebooting.")
            return True

    def logout(self, onttoken=None):
        """Step 6: Optional clean session logout"""
        try:
            if not onttoken:
                try:
                    onttoken = self.get_reboot_token()
                except Exception:
                    return
            logout_url = "/logout.cgi?RequestFile=/html/logout.html"
            self._request(
                logout_url,
                data={"x.X_HW_Token": onttoken},
                headers={"Referer": f"{self.base_url}/index.asp"}
            )
            logger.info("Cleanly logged out.")
        except Exception as e:
            logger.debug(f"Logout error (can be ignored): {e}")


def main():
    parser = argparse.ArgumentParser(description="Huawei ONT (HN8546X6N-20) Reboot Tool")
    parser.add_argument("--host", default="192.168.3.1", help="Router IP or URL (default: 192.168.3.1)")
    parser.add_argument("--user", default="user", help="Username (default: user)")
    parser.add_argument("--password", default=os.environ.get("MODERM_PASS", ""), help="Password (default: MODERM_PASS env var)")
    parser.add_argument("--check", "--dry-run", action="store_true", help="Dry run: verify login & token without rebooting")
    parser.add_argument("--reboot", action="store_true", help="Execute router reboot")
    parser.add_argument("--daily-at", type=str, default=None, help="Run as loop waiting until daily time (e.g. '04:30')")

    args = parser.parse_args()

    if not args.check and not args.reboot and not args.daily_at:
        print("Usage:")
        print("  Test connection: python reboot_modem.py --check")
        print("  Execute reboot:  python reboot_modem.py --reboot")
        print("  Daily schedule:  python reboot_modem.py --daily-at 04:30")
        print("Note: Set the password via MODERM_PASS environment variable or pass --password <pwd>")
        sys.exit(0)

    def do_work(is_reboot):
        client = ModemController(host=args.host, username=args.user, password=args.password)
        client.login()
        if is_reboot:
            client.reboot()
        else:
            token = client.get_reboot_token()
            logger.info(f"[DRY-RUN] Router connection and reboot token verified: {token}")
            client.logout(token)

    if args.daily_at:
        logger.info(f"Daemon mode started. Waiting to reboot every day at {args.daily_at}...")
        while True:
            now_str = time.strftime("%H:%M")
            if now_str == args.daily_at:
                logger.info(f"Target time {args.daily_at} reached. Triggering reboot...")
                try:
                    do_work(is_reboot=True)
                except Exception as e:
                    logger.error(f"Reboot execution error: {e}")
                # Sleep 70 seconds to avoid duplicate triggers within the same minute
                time.sleep(70)
            time.sleep(20)
    else:
        do_work(is_reboot=args.reboot)


if __name__ == "__main__":
    main()
