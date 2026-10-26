"""
FastAPI wrapper around the LangGraph workflow: start, status, review (JSON and email link).
State is kept in Postgres, so a restart no longer loses runs.

Run:
    docker compose up -d              # Postgres (see docker-compose.yml)
    pip install -r requirements.txt
    python run.py                     # on Windows use this, not plain `uvicorn app:app`

Endpoints:
    POST /v1/runs                 start a run          -> 202 + run_id
    GET  /v1/runs/{run_id}        status + live state  -> QUEUED / RUNNING / WAITING_REVIEW / COMPLETED / FAILED
    POST /v1/runs/{run_id}/review approve or reject    -> 202, the graph resumes in the background
    GET  /review/{token}          page opened from the email link (changes nothing)
    POST /review/{token}          the Approve / Reject buttons on that page -> resumes the graph

Where things are stored (all in one Postgres database, DATABASE_URL):
    runs table (db.py)            the API's bookkeeping: status, times, error, what the reviewer sees
    checkpoint tables             LangGraph's saved graph state (AsyncPostgresSaver)

Email: when a run pauses, the reviewer gets an email with a signed link (see notifier.py for the
environment variables). Without SMTP settings the email is printed in this terminal instead.

Where the "separation" is: the HTTP request ends as soon as the run is accepted. The graph runs
in a background task (Level 1). start_run() is the ONE place that decides where the graph runs,
so moving to a worker queue (Level 2) later means changing only that function.

Known limits of this step (fixed in later steps):
    - Keep ONE server process: background tasks live in the process, and at startup any run still
      RUNNING is assumed dead and marked FAILED.
    - No auth, no Idempotency-Key, no audit table yet.
"""
import asyncio
import logging
import os
import sys
import uuid
from contextlib import asynccontextmanager
from html import escape as esc
from typing import Literal

from fastapi import FastAPI, Form, HTTPException, Request
from fastapi.responses import HTMLResponse
from langgraph.checkpoint.postgres.aio import AsyncPostgresSaver
from langgraph.types import Command
from psycopg.rows import dict_row
from psycopg_pool import AsyncConnectionPool
from pydantic import BaseModel, Field

import db
import notifier
from graph import DEFAULT_TOKEN_BUDGET, build_graph

log = logging.getLogger("app")
RECURSION_LIMIT = 30
DEFAULT_DATABASE_URL = "postgresql://agent:agent@127.0.0.1:5432/agentdb"   # matches docker-compose.yml
TASKS: set[asyncio.Task] = set()  # strong references, so running tasks are not garbage-collected


def _require_compatible_event_loop() -> None:
    """psycopg cannot run on Windows' default (Proactor) loop. Fail with a clear message, not a
    confusing error from deep inside the driver."""
    loop = asyncio.get_running_loop()
    if sys.platform == "win32" and type(loop).__name__ == "ProactorEventLoop":
        raise RuntimeError(
            "psycopg needs the Windows selector event loop. Start the server with:  python run.py"
        )


@asynccontextmanager
async def lifespan(app: FastAPI):
    _require_compatible_event_loop()
    url = os.getenv("DATABASE_URL", DEFAULT_DATABASE_URL)
    # The LangGraph saver REQUIRES autocommit=True and dict rows on its connections.
    pool = AsyncConnectionPool(
        conninfo=url,
        min_size=1,
        max_size=10,
        kwargs={"autocommit": True, "prepare_threshold": 0, "row_factory": dict_row},
        open=False,
    )
    try:
        await pool.open(wait=True, timeout=10)
    except Exception as exc:
        await pool.close()
        raise RuntimeError(
            "Cannot connect to Postgres. Is Docker running and the database started "
            "(`docker compose up -d`)? Check DATABASE_URL."
        ) from exc
    try:
        checkpointer = AsyncPostgresSaver(pool)
        await checkpointer.setup()          # creates LangGraph's tables if they are missing
        await db.init_schema(pool)          # creates the runs table if it is missing
        stale = await db.fail_interrupted_runs(pool)
        if stale:
            log.warning("marked %d interrupted run(s) as FAILED", stale)
        app.state.pool = pool
        app.state.graph = build_graph(checkpointer)   # built ONCE, shared by every request
        yield
    finally:
        await pool.close()


app = FastAPI(title="Agent workflow API", lifespan=lifespan)


# ---------------------------------------------------------------- models

class StartRun(BaseModel):
    question: str = Field(min_length=1, max_length=2000)
    token_budget: int = Field(default=DEFAULT_TOKEN_BUDGET, ge=1, le=1_000_000)  # per-run cap


class Review(BaseModel):
    decision: Literal["approve", "reject"]
    comment: str = Field(default="", max_length=2000)


class RunOut(BaseModel):
    run_id: str
    status: Literal["QUEUED", "RUNNING", "WAITING_REVIEW", "COMPLETED", "FAILED"]
    outcome: Literal["APPROVED", "REJECTED"] | None = None
    created_at: str
    updated_at: str
    pending_review: dict | None = None   # what the reviewer needs to see
    result: dict | None = None           # final graph state
    error: str | None = None
    state: dict | None = None            # live graph state (GET only)
    next_node: list[str] | None = None   # which node runs or waits next (GET only)


# ---------------------------------------------------------------- running the graph

def start_run(app: FastAPI, run_id: str, graph_input) -> None:
    """The one place that decides WHERE the graph runs.

    Level 1 (now): a background task in this process.
    Level 2 (later): put a job on a queue here, and let a separate worker call _execute().
    """
    task = asyncio.create_task(_execute(app, run_id, graph_input))
    TASKS.add(task)
    task.add_done_callback(TASKS.discard)


async def _notify(run_id: str, payload: dict) -> None:
    """Send the review email. A mail problem must never fail the run: the run stays
    WAITING_REVIEW and can still be reviewed through /docs."""
    try:
        await notifier.notify_review_needed(run_id, payload)
    except Exception:
        log.exception("could not send the review email for %s", run_id)


async def _execute(app: FastAPI, run_id: str, graph_input) -> None:
    pool = app.state.pool
    # thread_id = this run's saved state. recursion_limit is the last-resort cap on graph steps,
    # so a routing bug cannot loop (and spend tokens) forever.
    config = {"configurable": {"thread_id": run_id}, "recursion_limit": RECURSION_LIMIT}
    try:
        await db.mark_running(pool, run_id)
        result = await app.state.graph.ainvoke(graph_input, config)
        if "__interrupt__" in result:
            # The graph paused at interrupt(). Nothing is running now, the state is saved.
            payload = result["__interrupt__"][0].value
            await db.mark_waiting(pool, run_id, payload)
            # Mail goes out AFTER the status flips, so a quick click never meets a 409.
            await _notify(run_id, payload)
        else:
            outcome = {"approve": "APPROVED", "reject": "REJECTED"}.get(result.get("approved"))
            await db.mark_completed(pool, run_id, result, outcome)
    except Exception as exc:  # background exceptions vanish silently unless we record them
        log.exception("run %s failed", run_id)
        try:
            await db.mark_failed(pool, run_id, f"{type(exc).__name__}: {exc}")
        except Exception:
            log.exception("could not record the failure of run %s", run_id)


# ---------------------------------------------------------------- endpoints

@app.post("/v1/runs", response_model=RunOut, status_code=202)
async def create_run(body: StartRun, request: Request):
    run_id = f"run_{uuid.uuid4().hex[:12]}"
    run = await db.insert_run(request.app.state.pool, run_id)   # the row exists BEFORE the task starts
    start_run(
        request.app,
        run_id,
        {"question": body.question, "retries": 0, "tokens_used": 0, "token_budget": body.token_budget},
    )
    return run


@app.get("/v1/runs/{run_id}", response_model=RunOut)
async def get_run(run_id: str, request: Request):
    run = await db.get_run(request.app.state.pool, run_id)
    if run is None:
        raise HTTPException(404, "Run not found")
    snapshot = await request.app.state.graph.aget_state({"configurable": {"thread_id": run_id}})
    return RunOut(**run, state=snapshot.values or None, next_node=list(snapshot.next))


async def submit_review(app: FastAPI, run_id: str, decision: str, comment: str) -> dict:
    """The ONE place a review is accepted. Used by the JSON endpoint and by the email page.
    The database makes the check-and-change atomic (db.claim_for_review): of any number of
    simultaneous approvals, exactly one is 'claimed'; the others get 409."""
    outcome = await db.claim_for_review(app.state.pool, run_id)
    if outcome == "not_found":
        raise HTTPException(404, "Run not found")
    if outcome != "claimed":
        raise HTTPException(409, f"Run is {outcome}, not WAITING_REVIEW")
    start_run(app, run_id, Command(resume={"decision": decision, "comment": comment}))
    return await db.get_run(app.state.pool, run_id)


@app.post("/v1/runs/{run_id}/review", response_model=RunOut, status_code=202)
async def review_run(run_id: str, body: Review, request: Request):
    return await submit_review(request.app, run_id, body.decision, body.comment)


# ---------------------------------------------------------------- review from the email link
# GET only SHOWS the page. Only the POST (a button press) changes anything, because mail scanners
# and link previewers open links automatically and must not be able to approve by accident.

_CSS = (
    "body{font-family:system-ui,sans-serif;max-width:640px;margin:2rem auto;padding:0 1rem;line-height:1.5}"
    ".box{border:1px solid #bbb;border-radius:8px;padding:1rem;white-space:pre-wrap;word-break:break-word}"
    "textarea{width:100%;box-sizing:border-box;margin:.5rem 0}"
    "button{font-size:1rem;padding:.6rem 1.2rem;margin-right:.5rem;border-radius:6px;border:1px solid #777;cursor:pointer}"
)


def _page(title: str, body_html: str, status: int = 200) -> HTMLResponse:
    doc = (
        '<!doctype html><html><head><meta charset="utf-8">'
        '<meta name="viewport" content="width=device-width, initial-scale=1">'
        f"<title>{esc(title)}</title><style>{_CSS}</style></head><body>{body_html}</body></html>"
    )
    return HTMLResponse(doc, status_code=status, headers={"Cache-Control": "no-store", "Referrer-Policy": "no-referrer"})


@app.get("/review/{token}", response_class=HTMLResponse, include_in_schema=False)
async def review_page(token: str, request: Request):
    try:
        run_id = notifier.read_token(token)
    except notifier.TokenError:
        return _page("Link not valid", "<h2>This link is not valid or has expired.</h2>", 400)
    run = await db.get_run(request.app.state.pool, run_id)
    if run is None:
        return _page("Run not found", "<h2>Run not found.</h2>", 404)
    if run["status"] != "WAITING_REVIEW":
        outcome = f" ({esc(run['outcome'])})" if run.get("outcome") else ""
        return _page("Already handled", f"<h2>Nothing to review.</h2><p>This run is {esc(run['status'])}{outcome}.</p>", 409)
    p = run.get("pending_review") or {}
    # Everything below came from a model or a user, so it is escaped before it goes into HTML.
    body = (
        f"<h2>Review needed</h2><p>Run <code>{esc(run_id)}</code><br>"
        f"Judge verdict: <b>{esc(str(p.get('verdict')))}</b> (stopped because: {esc(str(p.get('stop_reason')))})<br>"
        f"Tokens: {esc(str(p.get('tokens_used')))} of {esc(str(p.get('token_budget')))}</p>"
        f"<div class='box'>{esc(str(p.get('answer', '')))}</div>"
        "<form method='post'>"
        "<p><label>Comment (optional)<br><textarea name='comment' rows='3' maxlength='2000'></textarea></label></p>"
        "<button name='decision' value='approve'>Approve</button>"
        "<button name='decision' value='reject'>Reject</button>"
        "</form>"
    )
    return _page("Review needed", body)


@app.post("/review/{token}", response_class=HTMLResponse, include_in_schema=False)
async def review_submit(token: str, request: Request, decision: str = Form(...), comment: str = Form("")):
    try:
        run_id = notifier.read_token(token)
    except notifier.TokenError:
        return _page("Link not valid", "<h2>This link is not valid or has expired.</h2>", 400)
    if decision not in ("approve", "reject") or len(comment) > 2000:
        return _page("Invalid", "<h2>Invalid form.</h2>", 400)
    try:
        await submit_review(request.app, run_id, decision, comment)
    except HTTPException as exc:
        return _page("Not recorded", f"<h2>Not recorded.</h2><p>{esc(str(exc.detail))}</p>", exc.status_code)
    word = "Approved" if decision == "approve" else "Rejected"
    return _page("Recorded", f"<h2>{word}.</h2><p>Your decision was recorded and the run is continuing. You can close this tab.</p>")


@app.get("/health")
async def health():
    return {"status": "ok"}
