#!/usr/bin/env python3
"""Fresh-browser end-to-end journey for the visual marketing campaign builder.

The local mail stub is queried over HTTP and never records request headers or API-key values.
Set E2E_SHOTS_DIR to a scratch directory for development runs; the final run writes root evidence/.
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
import urllib.request
from datetime import datetime, timedelta
from pathlib import Path
from urllib.parse import urlsplit
from zoneinfo import ZoneInfo

from playwright.sync_api import TimeoutError as PlaywrightTimeoutError
from playwright.sync_api import sync_playwright

BASE = os.environ.get("E2E_BASE_URL", "http://127.0.0.1:5119").rstrip("/")
STUB_CONTROL = os.environ.get("E2E_STUB_CONTROL", "http://127.0.0.1:9998/control/captured")
SHOTS = Path(os.environ.get("E2E_SHOTS_DIR", "/tmp/marketing-builder-e2e-shots"))
TAG = (sys.argv[1] if len(sys.argv) > 1 else time.strftime("%Y%m%d%H%M%S"))
TAG = re.sub(r"[^A-Za-z0-9]", "", TAG)[:16].lower() or time.strftime("%H%M%S")
CHECKS: list[str] = []
CURRENT = [None]
OWNER_EMAIL = os.environ.get("E2E_OWNER_EMAIL", "owner@dechimexprinthub.com")
OWNER_NEW_PASSWORD = "OwnerE2E!2026xY"
TEST_EMAIL = f"marketing-test-{TAG}@example.com"


def check(name: str, condition: bool, detail: str = "") -> None:
    assert condition, f"FAILED: {name}" + (f" — {detail}" if detail else "")
    CHECKS.append(name)
    print(f"ok {len(CHECKS):02d} - {name}", flush=True)


def use(page):
    CURRENT[0] = page
    return page


def wait_for_text(page, selector: str, expected: str, timeout: int = 15000) -> None:
    locator = page.locator(selector)
    deadline = time.monotonic() + timeout / 1000
    last = ""
    while time.monotonic() < deadline:
        try:
            last = locator.inner_text(timeout=1000).strip()
            if last == expected:
                return
        except Exception:
            pass
        time.sleep(0.2)
    raise AssertionError(f"{selector} did not show {expected!r}; last text={last!r}")


def shot(name: str) -> None:
    SHOTS.mkdir(parents=True, exist_ok=True)
    CURRENT[0].mouse.move(270, 500)
    CURRENT[0].mouse.wheel(0, -10000)
    CURRENT[0].wait_for_timeout(120)
    CURRENT[0].screenshot(path=str(SHOTS / name), full_page=True, animations="disabled")
    print(f"shot - {name}", flush=True)


def goto(page, route: str):
    page.goto(BASE + route, wait_until="domcontentloaded")
    return page


def stub_messages() -> list[dict]:
    with urllib.request.urlopen(STUB_CONTROL, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def find_message(email: str, subject_fragment: str, timeout: int = 60) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for message in stub_messages():
            recipients = [str(item.get("email", "")).lower() for item in (message.get("to") or [])]
            if email.lower() in recipients and subject_fragment.lower() in str(message.get("subject", "")).lower():
                return message
        time.sleep(1)
    raise AssertionError(f"local mail stub did not receive the expected message for {email}")


def owner_login(page) -> None:
    goto(page, "/Account/Login")
    page.fill('form[action*="Login"] input[name="Email"]', OWNER_EMAIL)
    failures: list[str] = []
    for password in (OWNER_NEW_PASSWORD, "ChangeMe123!"):
        page.fill('form[action*="Login"] input[name="Password"]', password)
        page.click('form[action*="Login"] button[type="submit"]')
        try:
            page.wait_for_url(lambda url: "/account/login" not in str(url).lower(), timeout=8000)
            break
        except PlaywrightTimeoutError:
            body = page.locator("body").inner_text()
            failures.append(body[:300])
    else:
        raise AssertionError("owner login failed with the configured E2E bootstrap passwords")

    if "password" in page.url.lower() and "login" not in page.url.lower():
        page.fill('input[name="CurrentPassword"]', "ChangeMe123!")
        page.fill('input[name="NewPassword"]', OWNER_NEW_PASSWORD)
        page.fill('input[name="ConfirmPassword"]', OWNER_NEW_PASSWORD)
        page.click('form[action*="password"] button[type="submit"]')
        page.wait_for_timeout(1000)
    check("authorized Owner session is active", "/admin" in page.url.lower(), page.url)


def get_audience_counts(page) -> dict[str, int]:
    counts = []
    for segment in range(3):
        response = page.request.get(f"{BASE}/admin/marketing/audience-count?segment={segment}")
        if not response.ok:
            raise AssertionError(f"audience count request failed for supported segment {segment}: HTTP {response.status}")
        counts.append(int(response.json()["count"]))
    return {"all": counts[0], "purchasers": counts[1], "noPurchase90": counts[2]}


def create_customer_record(page, email: str, last_name: str, suffix: int) -> None:
    goto(page, "/admin/customers/create")
    page.locator("#FirstName").wait_for(timeout=15000)
    page.fill("#FirstName", "Marketing")
    page.fill("#LastName", last_name)
    page.fill("#Email", email)
    page.fill("#Phone", f"0803111000{suffix}")
    page.get_by_role("button", name="Create customer").click()
    page.wait_for_url(re.compile(r"/admin/customers/[0-9a-f-]{36}$", re.I), timeout=20000)
    check(f"Owner created a non-guest marketing customer record {suffix}", email.lower() in page.locator("body").inner_text().lower())


def stat_value(page, label: str) -> str:
    for row in page.locator(".card .flex.justify-between").all():
        if label in row.inner_text():
            return row.locator("b").inner_text()
    raise AssertionError(f"campaign details do not show the {label!r} outcome")


def wait_for_iframe_text(page, selector: str, expected_text: str, timeout: int = 25000):
    page.locator(selector).scroll_into_view_if_needed(timeout=5000)
    body = page.frame_locator(selector).locator("body")
    deadline = time.monotonic() + timeout / 1000
    last = ""
    while time.monotonic() < deadline:
        try:
            last = body.inner_text(timeout=1500)
            if expected_text in last:
                return body
        except Exception:
            pass
        time.sleep(0.25)
    raise AssertionError(f"iframe {selector} did not render expected text {expected_text!r}; last text={last[:240]!r}")


def wait_for_preview(page, expected_text: str) -> None:
    wait_for_iframe_text(page, "#previewFrame", expected_text)


def main() -> None:
    SHOTS.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        context = browser.new_context(
            viewport={"width": 1440, "height": 1000},
            device_scale_factor=1,
            timezone_id="Africa/Lagos",
            locale="en-NG",
        )
        owner = use(context.new_page())
        owner_login(owner)
        owner_counts_before = get_audience_counts(owner)
        check("campaign counts are queried through the authorized existing audience endpoint",
              set(owner_counts_before) == {"all", "purchasers", "noPurchase90"})
        goto(owner, "/admin/marketing")
        check("campaign management page is available to the Owner", "Marketing" in owner.locator("body").inner_text())
        shot("01-marketing-campaign-list.png")

        first_email = f"marketing-{TAG}-a@example.com"
        second_email = f"marketing-{TAG}-b@example.com"
        create_customer_record(owner, first_email, "PreviewA", 1)
        create_customer_record(owner, second_email, "PreviewB", 2)

        owner_counts_after = get_audience_counts(owner)
        check("Owner-created consented customer records increase the supported audience by two",
              owner_counts_after["all"] == owner_counts_before["all"] + 2,
              f"before={owner_counts_before}, after={owner_counts_after}")
        check("purchase-history segmentation excludes new non-purchasers",
              owner_counts_after["purchasers"] == owner_counts_before["purchasers"] and
              owner_counts_after["noPurchase90"] == owner_counts_before["noPurchase90"] + 2,
              f"before={owner_counts_before}, after={owner_counts_after}")

        goto(owner, "/admin/marketing/compose")
        owner.locator("#Name").wait_for(timeout=15000)
        owner.fill("#Name", f"Marketing studio journey {TAG}")
        owner.fill("#Subject", "A fresh print idea for your next project")
        owner.fill("#Preheader", "A concise update, designed for your inbox.")
        owner.fill("#SenderName", "DE-CHIMEX Campaign Studio")
        owner.fill("#ReplyToEmail", "campaigns@example.com")
        owner.select_option("#AudienceSegment", "1")
        wait_for_text(owner, "#audienceCount", str(owner_counts_after["purchasers"]))
        owner.select_option("#AudienceSegment", "2")
        wait_for_text(owner, "#audienceCount", str(owner_counts_after["noPurchase90"]))
        check("the editor displays only supported customer audience segments and their live counts",
              owner.locator("#AudienceSegment option").count() == 3)

        heading = f"A clear campaign note for {{{{FirstName}}}}"
        paragraph = "Hi {{FirstName}}, explore a fresh print idea picked for your next project."
        owner.locator('.campaign-block[data-index="0"] textarea').fill(heading)
        owner.locator('.campaign-block[data-index="1"] textarea').fill(paragraph)
        owner.locator('.campaign-block[data-index="2"] input[aria-label="Button label"]').fill("Explore the new collection")
        owner.locator('.campaign-block[data-index="2"] input[aria-label="Secure HTTPS link"]').fill("https://dechimexprinthub.com/shop")
        owner.fill("#Subject", "A fresh print idea for your next project")
        wait_for_preview(owner, "A clear campaign note")
        rendered = owner.frame_locator("#previewFrame").locator("body").inner_text()
        check("desktop preview renders composed content through the delivery renderer",
              "A clear campaign note" in rendered and "explore a fresh print idea" in rendered)
        check("preview retains the mandatory unsubscribe footer",
              owner.frame_locator("#previewFrame").locator('a[href*="unsubscribe"]').count() >= 1)
        shot("02-campaign-editor-desktop-preview.png")
        owner.click("#mobilePreview")
        check("mobile preview mode applies the compact device frame", "is-mobile" in owner.locator("#previewFrame").get_attribute("class"))
        shot("03-campaign-editor-mobile-preview.png")
        check("the editor provides controlled blocks, not a raw markup editor",
              owner.locator("#BlocksJson").count() == 1 and owner.locator("textarea[name*=Html]").count() == 0)

        owner.click("#saveCampaignButton")
        owner.wait_for_url(re.compile(r"/admin/marketing/[0-9a-f-]{36}$", re.I), timeout=20000)
        campaign_url = owner.url
        campaign_id = campaign_url.rstrip("/").rsplit("/", 1)[-1]
        check("campaign draft was persisted", "Draft" in owner.locator("body").inner_text())
        preview_route = owner.get_by_role("link", name="Open email preview").get_attribute("href")
        preview_response = owner.request.get(BASE + preview_route)
        saved_preview = preview_response.text()
        check("saved draft preview returns the delivery-rendered email",
              preview_response.ok and "A clear campaign note" in saved_preview and "unsubscribe" in saved_preview.lower())
        wait_for_iframe_text(owner, 'iframe[title="Rendered campaign email"]', "A clear campaign note", timeout=20000)
        check("campaign details embed the delivery-rendered email preview", True)
        preview_page = context.new_page()
        goto(preview_page, preview_route)
        use(preview_page)
        shot("04-saved-draft-rendered-email.png")
        preview_page.close()
        use(owner)

        owner.get_by_role("link", name="Edit draft").click()
        owner.wait_for_url(re.compile(r"/admin/marketing/[0-9a-f-]{36}/edit$", re.I), timeout=15000)
        original_paragraph = "Hi {{FirstName}}, explore a fresh print idea picked for your next project."
        owner.locator('.campaign-block[data-index="1"] textarea').wait_for(timeout=10000)
        check("draft content reopens intact after the first save",
              owner.locator('.campaign-block[data-index="1"] textarea').input_value() == original_paragraph)
        revised_paragraph = "Hi {{FirstName}}, see the latest print collection and plan your next project."
        owner.locator('.campaign-block[data-index="1"] textarea').fill(revised_paragraph)
        owner.click("#saveCampaignButton")
        owner.wait_for_url(re.compile(r"/admin/marketing/[0-9a-f-]{36}$", re.I), timeout=20000)
        owner.reload(wait_until="domcontentloaded")
        owner.get_by_role("link", name="Edit draft").click()
        owner.wait_for_url(re.compile(r"/admin/marketing/[0-9a-f-]{36}/edit$", re.I), timeout=15000)
        owner.locator('.campaign-block[data-index="1"] textarea').wait_for(timeout=10000)
        check("draft edits persist after a fresh page load",
              owner.locator('.campaign-block[data-index="1"] textarea').input_value() == revised_paragraph)
        owner.goto(campaign_url, wait_until="domcontentloaded")
        check("saved and edited campaign returns to the draft details view", "Draft" in owner.locator("body").inner_text())

        owner.fill("#testEmail", TEST_EMAIL)
        owner.get_by_role("button", name="Send test").click()
        owner.get_by_text("Test email accepted for").last.wait_for(timeout=20000)
        test_message = find_message(TEST_EMAIL, "[TEST] A fresh print idea", timeout=5)
        check("test send is accepted by the provider adapter with the campaign sender",
              (test_message.get("sender") or {}).get("name") == "DE-CHIMEX Campaign Studio")
        check("test send uses the campaign reply-to address",
              (test_message.get("replyTo") or {}).get("email") == "campaigns@example.com")
        check("test message uses safe personalization and mandatory unsubscribe handling",
              "Alex" in test_message.get("htmlContent", "") and
              "unsubscribe" in test_message.get("htmlContent", "").lower())
        check("test send does not snapshot or contact campaign customers",
              re.search(r"Recipient snapshot\s+0(?:\s|$)", owner.locator("body").inner_text()) is not None)
        shot("05-test-send-confirmed-no-customer-contact.png")

        owner.get_by_role("link", name="Review send").click()
        owner.wait_for_url(re.compile(r"/admin/marketing/[0-9a-f-]{36}/confirm$", re.I), timeout=15000)
        confirm_text = owner.locator("body").inner_text()
        check("explicit confirmation shows the selected audience and exact eligible count",
              "No paid purchase in the last 90 days" in confirm_text and
              f"eligible customer(s)" in confirm_text and
              owner.locator("#ExpectedRecipientCount").input_value() == str(owner_counts_after["noPurchase90"]))
        check("confirmation offers both immediate send and scheduling",
              owner.locator('input[name="SendMode"][value="now"]').count() == 1 and
              owner.locator('input[name="SendMode"][value="schedule"]').count() == 1)
        shot("06-explicit-send-confirmation.png")

        # datetime-local uses browser-minute precision; round up beyond the server-side
        # one-minute minimum so every current second remains valid across timezones.
        now_local = datetime.now(ZoneInfo("Africa/Lagos"))
        schedule_at = (now_local + timedelta(minutes=1)).replace(second=0, microsecond=0) + timedelta(minutes=1)
        owner.locator('input[name="SendMode"][value="schedule"]').check()
        owner.fill("#scheduledLocal", schedule_at.strftime("%Y-%m-%dT%H:%M"))
        owner.locator('input[name="Confirm"]').check()
        owner.get_by_role("button", name="Confirm schedule").click()
        owner.wait_for_url(re.compile(r"/admin/marketing/[0-9a-f-]{36}$", re.I), timeout=20000)
        owner.get_by_text("Scheduled", exact=True).wait_for(timeout=10000)
        check("explicitly confirmed campaign was scheduled", True)
        shot("07-campaign-scheduled.png")

        deadline = time.monotonic() + 155
        final_text = ""
        while time.monotonic() < deadline:
            owner.reload(wait_until="domcontentloaded")
            final_text = owner.locator("body").inner_text()
            if "Completed" in final_text and "Awaiting an outcome" in final_text:
                try:
                    if (stat_value(owner, "Accepted by email service") == str(owner_counts_after["noPurchase90"]) and
                            stat_value(owner, "Failed") == "0"):
                        break
                except Exception:
                    pass
            time.sleep(2)
        check("background worker delivered to the confirmed consented audience",
              "Completed" in final_text and
              stat_value(owner, "Accepted by email service") == str(owner_counts_after["noPurchase90"]) and
              stat_value(owner, "Failed") == "0",
              "campaign did not reach the expected successful terminal state before timeout")

        campaign_messages = [message for message in stub_messages()
                             if message.get("subject") == "A fresh print idea for your next project"]
        delivered_recipients = []
        for message in campaign_messages:
            delivered_recipients.extend(message.get("to") or [])
            check("broadcast carries the selected sender and reply-to settings",
                  (message.get("sender") or {}).get("name") == "DE-CHIMEX Campaign Studio" and
                  (message.get("replyTo") or {}).get("email") == "campaigns@example.com")
            check("broadcast includes an unsubscribe link and recipient personalization",
                  "unsubscribe" in message.get("htmlContent", "").lower() and
                  "Marketing" in message.get("htmlContent", ""))
        broadcast_emails = {str(recipient.get("email", "")).lower() for recipient in delivered_recipients}
        check("broadcast reaches each selected audience member exactly once",
              broadcast_emails == {first_email, second_email} and len(delivered_recipients) == 2,
              f"expected two selected recipients, received {len(delivered_recipients)}")
        check("provider adapter reports accepted messages without overstating delivery analytics",
              "Accepted by email service" in final_text and "opens, and clicks are not reported" in final_text)
        shot("08-campaign-completed-accepted-by-email-service.png")

        first_delivery = next(message for message in campaign_messages
                              if first_email in [str(item.get("email", "")).lower() for item in (message.get("to") or [])])
        link_match = re.search(r'''href=["']([^"']*/unsubscribe/([0-9a-fA-F-]{36}))["']''',
                               first_delivery.get("htmlContent", ""), re.I)
        check("delivered message contains a tokenized unsubscribe link", link_match is not None)
        unsubscribe_path = urlsplit(link_match.group(1)).path
        goto(owner, unsubscribe_path)
        owner.get_by_role("button", name="Yes, unsubscribe me").click()
        check("public unsubscribe flow confirms the opt-out", "unsubscribed" in owner.locator("body").inner_text().lower())
        counts_after_optout = get_audience_counts(owner)
        check("future audience counts exclude the opted-out customer",
              counts_after_optout["all"] == owner_counts_after["all"] - 1 and
              counts_after_optout["noPurchase90"] == owner_counts_after["noPurchase90"] - 1,
              f"before={owner_counts_after}, after={counts_after_optout}")
        shot("09-unsubscribe-confirmation-and-optout.png")

        summary = {
            "result": "PASS",
            "journey": [
                "owner authentication", "supported consent-aware audience counts", "two Owner-created consented CRM records",
                "controlled block design", "desktop and mobile previews", "draft save and edit persistence",
                "provider-backed test send", "explicit scheduled-send confirmation", "background processing and accepted outcomes",
                "unsubscribe and personalization assertions"
            ],
            "checks": CHECKS,
            "checkCount": len(CHECKS),
            "campaignId": campaign_id,
            "campaignStatus": "Completed",
            "audienceCount": owner_counts_after["noPurchase90"],
            "providerStubCaptureCount": len(stub_messages()),
            "screenshots": sorted(path.name for path in SHOTS.glob("*.png")),
        }
        summary_path = SHOTS / "marketing-builder-e2e-summary.json"
        summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(json.dumps(summary, indent=2, ensure_ascii=False), flush=True)
        browser.close()


if __name__ == "__main__":
    main()
