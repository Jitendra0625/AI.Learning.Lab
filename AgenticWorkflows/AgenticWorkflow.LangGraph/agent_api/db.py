"""
The `runs` table: the API's own bookkeeping (status, times, error, what the reviewer must see).

LangGraph keeps the graph's saved state (checkpoints) in the SAME database, in its own tables,
written by AsyncPostgresSaver. Two kinds of data, one database:

    runs                 <- this file: "what is run_abc doing right now?"
    checkpoints, ...     <- LangGraph:  "where exactly is the graph, and what is in its state?"

Every function takes the connection pool and uses one short statement. Each statement commits on
its own (the pool is opened with autocommit=True, which the LangGraph saver requires).
"""
import json

from psycopg.types.json import Jsonb

# One statement per entry: the pool prepares every statement (prepare_threshold=0, which the
# LangGraph saver needs), and a prepared statement cannot contain several commands.
SCHEMA = [
    """
    CREATE TABLE IF NOT EXISTS runs (
        run_id          text PRIMARY KEY,
        status          text NOT NULL,
        outcome         text,
        created_at      timestamptz NOT NULL DEFAULT now(),
        updated_at      timestamptz NOT NULL DEFAULT now(),
        pending_review  jsonb,
        result          jsonb,
        error           text
    )
    """,
    "CREATE INDEX IF NOT EXISTS runs_status_idx ON runs (status)",
]


def _json(value):
    # default=str: never let one odd value in the graph state turn a finished run into a failure
    return Jsonb(value, dumps=lambda obj: json.dumps(obj, default=str))


def _clean(row: dict) -> dict:
    out = dict(row)
    for key in ("created_at", "updated_at"):
        out[key] = out[key].isoformat(timespec="seconds")
    return out


async def _one(pool, sql: str, params=None):
    """Run a statement that returns a row (SELECT, or UPDATE/INSERT ... RETURNING)."""
    async with pool.connection() as conn:
        cur = await conn.execute(sql, params)
        return await cur.fetchone()


async def _run(pool, sql: str, params=None) -> int:
    """Run a statement that returns nothing. Gives back how many rows it changed."""
    async with pool.connection() as conn:
        cur = await conn.execute(sql, params)
        return cur.rowcount


async def init_schema(pool) -> None:
    for statement in SCHEMA:
        await _run(pool, statement)


async def insert_run(pool, run_id: str) -> dict:
    row = await _one(pool, "INSERT INTO runs (run_id, status) VALUES (%s, 'QUEUED') RETURNING *", (run_id,))
    return _clean(row)


async def get_run(pool, run_id: str) -> dict | None:
    row = await _one(pool, "SELECT * FROM runs WHERE run_id = %s", (run_id,))
    return _clean(row) if row else None


async def mark_running(pool, run_id: str) -> None:
    await _run(pool, "UPDATE runs SET status = 'RUNNING', updated_at = now() WHERE run_id = %s", (run_id,))


async def mark_waiting(pool, run_id: str, payload: dict) -> None:
    await _run(
        pool,
        "UPDATE runs SET status = 'WAITING_REVIEW', pending_review = %s, updated_at = now() WHERE run_id = %s",
        (_json(payload), run_id),
    )


async def mark_completed(pool, run_id: str, result: dict, outcome: str | None) -> None:
    await _run(
        pool,
        "UPDATE runs SET status = 'COMPLETED', pending_review = NULL, result = %s, outcome = %s,"
        " updated_at = now() WHERE run_id = %s",
        (_json(result), outcome, run_id),
    )


async def mark_failed(pool, run_id: str, error: str) -> None:
    await _run(
        pool,
        "UPDATE runs SET status = 'FAILED', error = %s, updated_at = now() WHERE run_id = %s",
        (error, run_id),
    )


async def claim_for_review(pool, run_id: str) -> str:
    """Take the review, atomically. Returns 'claimed', 'not_found', or the run's current status.

    The check ("is it waiting?") and the change ("now it is running") are ONE statement, so the
    database lets exactly one of any number of simultaneous callers win. This is the same idea as
    the atomic stock decrement, and it keeps working with several server processes.
    """
    row = await _one(
        pool,
        "UPDATE runs SET status = 'RUNNING', pending_review = NULL, updated_at = now()"
        " WHERE run_id = %s AND status = 'WAITING_REVIEW' RETURNING run_id",
        (run_id,),
    )
    if row:
        return "claimed"
    current = await _one(pool, "SELECT status FROM runs WHERE run_id = %s", (run_id,))
    return "not_found" if current is None else current["status"]


async def fail_interrupted_runs(pool) -> int:
    """At startup: a run still QUEUED or RUNNING belonged to a server that died. Mark it FAILED
    instead of leaving it 'running' forever. Runs WAITING_REVIEW are fine and are left alone.
    Assumes ONE server process; with several, a starting server would fail the others' live runs."""
    return await _run(
        pool,
        "UPDATE runs SET status = 'FAILED', error = 'Interrupted by a server restart while the graph was running',"
        " updated_at = now() WHERE status IN ('QUEUED', 'RUNNING')",
    )
