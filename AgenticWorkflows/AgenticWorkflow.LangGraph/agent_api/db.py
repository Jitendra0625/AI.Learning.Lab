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
    # One row per review decision. UNIQUE (run_id, gate): a review can be decided only once.
    # ON DELETE CASCADE: deleting a run (for example `delete from runs;` while developing) removes its history too.
    """
    CREATE TABLE IF NOT EXISTS audit_log (
        id              bigserial PRIMARY KEY,
        run_id          text NOT NULL REFERENCES runs (run_id) ON DELETE CASCADE,
        gate            text NOT NULL,
        stage           text NOT NULL,
        attempt         integer NOT NULL,
        decision        text NOT NULL CHECK (decision IN ('approve', 'reject')),
        comment         text NOT NULL DEFAULT '',
        via             text NOT NULL CHECK (via IN ('api', 'email_link')),
        attempt_tokens  integer,
        tokens_used     integer,
        artifact_hashes jsonb NOT NULL DEFAULT '[]'::jsonb,
        created_at      timestamptz NOT NULL DEFAULT now(),
        UNIQUE (run_id, gate)
    )
    """,
    # The files each attempt of each stage produced, kept for every attempt (rejected ones too).
    """
    CREATE TABLE IF NOT EXISTS artifacts (
        id         bigserial PRIMARY KEY,
        run_id     text NOT NULL REFERENCES runs (run_id) ON DELETE CASCADE,
        stage      text NOT NULL,
        attempt    integer NOT NULL,
        path       text NOT NULL,
        content    text NOT NULL,
        sha256     text NOT NULL,
        bytes      integer NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        UNIQUE (run_id, stage, attempt, path)
    )
    """,
]


def _json(value):
    # default=str: never let one odd value in the graph state turn a finished run into a failure
    return Jsonb(value, dumps=lambda obj: json.dumps(obj, default=str))


def _clean(row: dict) -> dict:
    out = dict(row)
    for key in ("created_at", "updated_at"):
        if key in out:
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


_CLAIM_AND_AUDIT = """
WITH waiting AS (
    SELECT run_id, pending_review
    FROM runs
    WHERE run_id = %(run_id)s AND status = 'WAITING_REVIEW' AND pending_review->>'gate' = %(gate)s
    FOR UPDATE
), claimed AS (
    UPDATE runs r
    SET status = 'RUNNING', pending_review = NULL, updated_at = now()
    FROM waiting w
    WHERE r.run_id = w.run_id
    RETURNING r.run_id, w.pending_review AS p
)
INSERT INTO audit_log (run_id, gate, stage, attempt, decision, comment, via, attempt_tokens, tokens_used, artifact_hashes)
SELECT
    run_id,
    %(gate)s::text,
    p->>'stage',
    (p->>'attempt')::int,
    %(decision)s::text,
    %(comment)s::text,
    %(via)s::text,
    (p->>'attempt_tokens')::int,
    (p->>'tokens_used')::int,
    COALESCE(
        (SELECT jsonb_agg(jsonb_build_object('path', f->>'path', 'sha256', f->>'sha256'))
         FROM jsonb_array_elements(COALESCE(p->'artifacts', '[]'::jsonb)) AS f),
        '[]'::jsonb
    )
FROM claimed
RETURNING run_id
"""


async def claim_for_review(pool, run_id: str, gate: str, decision: str, comment: str, via: str) -> str:
    """Take the review AND write its audit row, atomically.
    Returns 'claimed', 'not_found', 'stale_gate', or the run's current status.

    ONE statement does all of this: it finds the run only if it is waiting at THIS gate (for
    example "design_plan:2"), marks it RUNNING, and inserts the audit row. So:
      * of any number of simultaneous callers, exactly one wins (the others find no waiting row);
      * a decision for an earlier review (old email link, a late double click) cannot touch a later one;
      * a decision can never be accepted without being recorded, and never recorded without being accepted.
    """
    row = await _one(
        pool,
        _CLAIM_AND_AUDIT,
        {"run_id": run_id, "gate": gate, "decision": decision, "comment": comment, "via": via},
    )
    if row:
        return "claimed"
    current = await _one(pool, "SELECT status FROM runs WHERE run_id = %s", (run_id,))
    if current is None:
        return "not_found"
    if current["status"] == "WAITING_REVIEW":
        return "stale_gate"     # waiting, but for a different review than the caller named
    return current["status"]


async def save_artifacts(pool, run_id: str, stage: str, attempt: int, artifacts: list[dict]) -> None:
    """Store the files of one attempt, all or none (one transaction). Safe to call twice for the
    same attempt: a file already stored is left as it is."""
    async with pool.connection() as conn:
        async with conn.transaction():
            for a in artifacts:
                await conn.execute(
                    "INSERT INTO artifacts (run_id, stage, attempt, path, content, sha256, bytes)"
                    " VALUES (%s, %s, %s, %s, %s, %s, %s)"
                    " ON CONFLICT (run_id, stage, attempt, path) DO NOTHING",
                    (run_id, stage, attempt, a["path"], a["content"], a["sha256"], a["bytes"]),
                )


async def list_artifacts(pool, run_id: str) -> list[dict]:
    """The files of a run WITHOUT their content (use get_artifact for that), oldest first."""
    async with pool.connection() as conn:
        cur = await conn.execute(
            "SELECT id, stage, attempt, path, sha256, bytes, created_at FROM artifacts"
            " WHERE run_id = %s ORDER BY id",
            (run_id,),
        )
        return [_clean(r) for r in await cur.fetchall()]


async def get_artifact(pool, run_id: str, artifact_id: int) -> dict | None:
    row = await _one(
        pool,
        "SELECT id, stage, attempt, path, content, sha256, bytes, created_at FROM artifacts"
        " WHERE run_id = %s AND id = %s",
        (run_id, artifact_id),
    )
    return _clean(row) if row else None


async def list_audit(pool, run_id: str) -> list[dict]:
    async with pool.connection() as conn:
        cur = await conn.execute(
            "SELECT id, gate, stage, attempt, decision, comment, via, attempt_tokens, tokens_used, artifact_hashes, created_at"
            " FROM audit_log WHERE run_id = %s ORDER BY id",
            (run_id,),
        )
        return [_clean(r) for r in await cur.fetchall()]


async def fail_interrupted_runs(pool) -> int:
    """At startup: a run still QUEUED or RUNNING belonged to a server that died. Mark it FAILED
    instead of leaving it 'running' forever. Runs WAITING_REVIEW are fine and are left alone.
    Assumes ONE server process; with several, a starting server would fail the others' live runs."""
    return await _run(
        pool,
        "UPDATE runs SET status = 'FAILED', error = 'Interrupted by a server restart while the graph was running',"
        " updated_at = now() WHERE status IN ('QUEUED', 'RUNNING')",
    )
