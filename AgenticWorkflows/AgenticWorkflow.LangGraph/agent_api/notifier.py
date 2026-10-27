"""
Email + signed review links for the human-approval step.

Flow:
    graph pauses  ->  app.py calls notify_review_needed()  ->  you get an email with a link
    link opens a confirmation page (GET, changes nothing)  ->  you press Approve or Reject (POST)
    ->  the run resumes.

Settings come from environment variables (nothing secret lives in the code):

    REVIEWER_EMAIL       who gets the email. If empty, the link is only printed in the server terminal.
    SMTP_HOST            e.g. smtp.gmail.com. If empty, the email is printed in the terminal, not sent.
    SMTP_PORT            default 587 (STARTTLS)
    SMTP_USER            SMTP login (Gmail: your address)
    SMTP_PASSWORD        SMTP password (Gmail: an "app password", needs 2-step verification)
    MAIL_FROM            default: SMTP_USER
    PUBLIC_BASE_URL      default http://127.0.0.1:8000  (a link to 127.0.0.1 only opens on this computer)
    REVIEW_LINK_SECRET   signs the links. SET THIS to a long random string; the default is for local dev only.
    REVIEW_LINK_TTL_SECONDS  default 86400 (24 hours)
"""
import asyncio
import base64
import hashlib
import hmac
import logging
import os
import smtplib
import time
from email.message import EmailMessage

log = logging.getLogger("notifier")

_DEV_SECRET = "dev-only-secret-change-me"
if "REVIEW_LINK_SECRET" not in os.environ:
    log.warning("REVIEW_LINK_SECRET is not set: using the development secret. Do not deploy like this.")


class TokenError(ValueError):
    """The link is malformed, tampered with, or expired."""


# ---------------------------------------------------------------- signed links

def _secret() -> bytes:
    return os.getenv("REVIEW_LINK_SECRET", _DEV_SECRET).encode()


def _ttl() -> int:
    return int(os.getenv("REVIEW_LINK_TTL_SECONDS", "86400"))


def _b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode().rstrip("=")


def _unb64(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def _sign(body: bytes) -> bytes:
    return hmac.new(_secret(), body, hashlib.sha256).digest()


def make_token(run_id: str, gate: str, ttl: int | None = None, now: float | None = None) -> str:
    """run_id + gate + expiry, signed with HMAC. Anyone can read it, nobody can change it without
    the secret.

    The GATE (which review: "designer:2" = the designer's second attempt) is part of the link
    because one run now has several reviews. A link from an earlier email must not be able to
    approve a LATER review of the same run. app.py checks the gate against the run's current one.
    """
    current = time.time() if now is None else now
    expires = int(current + (_ttl() if ttl is None else ttl))
    body = f"{run_id}|{gate}|{expires}".encode()
    return f"{_b64(body)}.{_b64(_sign(body))}"


def read_token(token: str, now: float | None = None) -> tuple[str, str]:
    """Return (run_id, gate) from a valid token, or raise TokenError."""
    try:
        body_part, sig_part = token.split(".")
        body, sig = _unb64(body_part), _unb64(sig_part)
        run_id, gate, expires = body.decode().split("|")
        expires_at = int(expires)
    except Exception:
        raise TokenError("malformed") from None
    if not hmac.compare_digest(sig, _sign(body)):   # constant-time comparison
        raise TokenError("bad signature")
    if (time.time() if now is None else now) > expires_at:
        raise TokenError("expired")
    return run_id, gate


# ---------------------------------------------------------------- sending

def send_email(to: str, subject: str, body: str) -> None:
    """Blocking. Called through asyncio.to_thread so it never freezes the event loop.
    With no SMTP_HOST it prints the email instead (local development)."""
    host = os.getenv("SMTP_HOST")
    if not host:
        print(f"\n--- email (SMTP_HOST not set, printing instead) ---\nTo: {to}\nSubject: {subject}\n\n{body}\n---\n")
        return
    msg = EmailMessage()
    msg["From"] = os.getenv("MAIL_FROM") or os.getenv("SMTP_USER", "")
    msg["To"] = to
    msg["Subject"] = subject
    msg.set_content(body)
    with smtplib.SMTP(host, int(os.getenv("SMTP_PORT", "587")), timeout=15) as smtp:
        smtp.starttls()
        user = os.getenv("SMTP_USER")
        if user:
            smtp.login(user, os.getenv("SMTP_PASSWORD", ""))
        smtp.send_message(msg)


def review_link(run_id: str, gate: str) -> str:
    return f"{os.getenv('PUBLIC_BASE_URL', 'http://127.0.0.1:8000').rstrip('/')}/review/{make_token(run_id, gate)}"


async def notify_review_needed(run_id: str, payload: dict) -> None:
    """Tell the reviewer a run is waiting. `payload` is what the review node passed to interrupt()."""
    link = review_link(run_id, payload["gate"])
    hours = max(1, _ttl() // 3600)
    output = str(payload.get("output", ""))
    if len(output) > 1500:
        output = output[:1500] + " ..."
    where = f"step {payload.get('step')}/{payload.get('total_steps')}, {payload.get('title')}"
    subject = f"Review needed: {run_id} ({where}, attempt {payload.get('attempt')}/{payload.get('max_attempts')})"
    earlier = (
        f"This attempt was asked to address your earlier comment:\n{payload['previous_feedback']}\n\n"
        if payload.get("previous_feedback")
        else ""
    )
    body = (
        "A run is waiting for your review.\n\n"
        f"Run:      {run_id}\n"
        f"Stage:    {where}\n"
        f"Attempt:  {payload.get('attempt')} of {payload.get('max_attempts')}\n"
        f"Tokens:   {payload.get('tokens_used')} of {payload.get('token_budget')}\n\n"
        f"{earlier}"
        f"Result:\n{output}\n\n"
        "Open this page to approve or reject:\n"
        f"{link}\n\n"
        f"The link works for {hours} hours. Opening it changes nothing; you confirm on the page.\n"
    )
    to = os.getenv("REVIEWER_EMAIL", "")
    if not to:
        print(f"\n[REVIEWER_EMAIL not set] review link for {run_id}:\n{link}\n")
        return
    await asyncio.to_thread(send_email, to, subject, body)
