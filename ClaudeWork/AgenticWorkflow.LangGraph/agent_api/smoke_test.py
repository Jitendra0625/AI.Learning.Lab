"""
End-to-end smoke test: python smoke_test.py
Uses the real graph with its fake agents. No server needed (FastAPI's TestClient runs the app),
but Postgres must be running:  docker compose up -d   (or set DATABASE_URL to another database)
"""
import asyncio
import os
import re
import sys
import time

if sys.platform == "win32":   # psycopg cannot use Windows' default event loop
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

os.environ["REVIEWER_EMAIL"] = "reviewer@example.com"   # set before the app sends anything
os.environ["REVIEW_LINK_SECRET"] = "test-secret"
os.environ.setdefault("DATABASE_URL", "postgresql://agent:agent@127.0.0.1:5432/agentdb")

from fastapi.testclient import TestClient

import notifier
from app import app

# Capture emails instead of sending them.
sent = []
notifier.send_email = lambda to, subject, body: sent.append((to, subject, body))


def wait_until(condition, timeout=3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if condition():
            return
        time.sleep(0.05)
    raise AssertionError("condition not reached in time")


def wait_for(client, run_id, wanted, timeout=5.0):
    """Poll the status endpoint until the run reaches one of the wanted statuses."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        body = client.get(f"/v1/runs/{run_id}").json()
        if body["status"] in wanted:
            return body
        time.sleep(0.05)
    raise AssertionError(f"timed out waiting for {wanted}, last status {body['status']}")


with TestClient(app) as client:   # the `with` keeps the app (and its background tasks) alive
    # 1. start: the request ends immediately with 202
    r = client.post("/v1/runs", json={"question": "What does MCOB say about affordability?"})
    assert r.status_code == 202, r.text
    run_id = r.json()["run_id"]
    print("started:", run_id, r.json()["status"])

    # 2. the graph runs in the background and pauses for review
    waiting = wait_for(client, run_id, {"WAITING_REVIEW"})
    print("paused :", waiting["status"], "| next node:", waiting["next_node"])
    print("review payload:", waiting["pending_review"])
    assert waiting["pending_review"]["retries"] == 3
    assert waiting["next_node"] == ["approve"]

    # 3. two reviews back to back: one wins, the other gets 409
    first = client.post(f"/v1/runs/{run_id}/review", json={"decision": "approve", "comment": "looks right"})
    second = client.post(f"/v1/runs/{run_id}/review", json={"decision": "approve"})
    print("review 1:", first.status_code, "| review 2 (double click):", second.status_code)
    assert (first.status_code, second.status_code) == (202, 409)

    # 4. the run completes, and the decision is no longer lost
    done = wait_for(client, run_id, {"COMPLETED", "FAILED"})
    print("final  :", done["status"], done["outcome"], "| comment:", done["result"]["review_comment"])
    assert done["status"] == "COMPLETED" and done["outcome"] == "APPROVED"
    assert done["result"]["approved"] == "approve"

    # 5. reviewing a finished run is rejected
    assert client.post(f"/v1/runs/{run_id}/review", json={"decision": "approve"}).status_code == 409

    # 6. a rejection is recorded too
    run2 = client.post("/v1/runs", json={"question": "second question"}).json()["run_id"]
    wait_for(client, run2, {"WAITING_REVIEW"})
    client.post(f"/v1/runs/{run2}/review", json={"decision": "reject", "comment": "wrong section"})
    done2 = wait_for(client, run2, {"COMPLETED"})
    print("reject :", done2["outcome"], "| comment:", done2["result"]["review_comment"])
    assert done2["outcome"] == "REJECTED"

    # 7. input validation and unknown ids
    assert client.post("/v1/runs", json={"question": ""}).status_code == 422
    assert client.post(f"/v1/runs/{run2}/review", json={"decision": "maybe"}).status_code == 422
    assert client.get("/v1/runs/run_missing").status_code == 404

    assert client.post("/v1/runs", json={"question": "x", "token_budget": 0}).status_code == 422

    # 7b. a spent token budget stops the retry loop early and tells the reviewer why
    run_b = client.post("/v1/runs", json={"question": "tiny budget", "token_budget": 1500}).json()["run_id"]
    paused_b = wait_for(client, run_b, {"WAITING_REVIEW"})
    print("budget :", paused_b["pending_review"])
    assert paused_b["pending_review"]["stop_reason"] == "budget"
    assert paused_b["pending_review"]["retries"] == 1          # stopped after the first attempt
    assert waiting["pending_review"]["stop_reason"] == "max_retries"   # default budget: retry cap hit

    # 7c. review by email link: mail -> page -> button -> run resumes
    # The question is hostile on purpose: it flows into the answer, which goes into the page.
    mails_before = len(sent)
    run_e = client.post("/v1/runs", json={"question": "<script>alert(1)</script>"}).json()["run_id"]
    wait_for(client, run_e, {"WAITING_REVIEW"})
    wait_until(lambda: len(sent) > mails_before)
    to, subject, mail_body = sent[-1]
    assert to == "reviewer@example.com" and run_e in subject
    path = re.search(r"/review/\S+", mail_body).group(0)

    page = client.get(path)
    assert page.status_code == 200 and "Approve" in page.text and "Reject" in page.text
    assert "<script>alert" not in page.text and "&lt;script&gt;" in page.text   # model/user text is escaped
    assert client.get(f"/v1/runs/{run_e}").json()["status"] == "WAITING_REVIEW"  # opening the link changed nothing

    assert client.get(path[:-3] + "abc").status_code == 400                       # tampered link
    expired = "/review/" + notifier.make_token(run_e, ttl=-10)
    assert client.get(expired).status_code == 400                                  # expired link
    assert client.post(expired, data={"decision": "approve"}).status_code == 400
    assert client.post(path, data={"decision": "maybe"}).status_code == 400        # bad form value

    ok = client.post(path, data={"decision": "approve", "comment": "via email"})
    assert ok.status_code == 200 and "Approved" in ok.text
    done_e = wait_for(client, run_e, {"COMPLETED"})
    assert done_e["outcome"] == "APPROVED" and done_e["result"]["review_comment"] == "via email"
    assert client.post(path, data={"decision": "approve"}).status_code == 409      # second click
    assert client.get(path).status_code == 409                                     # page after the decision
    print("email  : link works, page escaped, tampered/expired/double click rejected")

    # a mail failure must not fail the run
    def broken_mail(to, subject, body):
        raise OSError("smtp down")
    notifier.send_email = broken_mail
    run_m = client.post("/v1/runs", json={"question": "mail will fail"}).json()["run_id"]
    wait_for(client, run_m, {"WAITING_REVIEW"})
    time.sleep(0.3)
    assert client.get(f"/v1/runs/{run_m}").json()["status"] == "WAITING_REVIEW"
    notifier.send_email = lambda to, subject, body: sent.append((to, subject, body))

    # 8. an exception inside the graph is recorded as FAILED, not swallowed
    class BrokenGraph:
        async def ainvoke(self, *args, **kwargs):
            raise RuntimeError("boom")

        async def aget_state(self, config):
            class Snap:
                values, next = {}, ()
            return Snap()

    real_graph = app.state.graph
    app.state.graph = BrokenGraph()
    run3 = client.post("/v1/runs", json={"question": "this will fail"}).json()["run_id"]
    failed = wait_for(client, run3, {"FAILED"})
    print("failed :", failed["status"], "|", failed["error"])
    app.state.graph = real_graph

# ---- persistence checks: this is what Postgres buys you
import psycopg
from psycopg.rows import dict_row
from psycopg_pool import AsyncConnectionPool

import db

# 11. restart survival: a run waiting for review is still there, and still resumable, after the
#     whole app (pool, checkpointer, graph, memory) has been shut down and started again.
sent_before = len(sent)
with TestClient(app) as c1:
    rid = c1.post("/v1/runs", json={"question": "survive a restart"}).json()["run_id"]
    wait_for(c1, rid, {"WAITING_REVIEW"})
    wait_until(lambda: len(sent) > sent_before)          # let the mail step finish before shutdown
# the with-block ended: the app shut down and everything held in memory is gone
with TestClient(app) as c2:                               # a fresh startup: new pool, saver and graph
    after = c2.get(f"/v1/runs/{rid}").json()
    assert after["status"] == "WAITING_REVIEW" and after["next_node"] == ["approve"], after
    assert after["pending_review"]["retries"] == 3
    ok = c2.post(f"/v1/runs/{rid}/review", json={"decision": "approve", "comment": "after restart"})
    assert ok.status_code == 202
    finished = wait_for(c2, rid, {"COMPLETED"})
    assert finished["outcome"] == "APPROVED" and finished["result"]["review_comment"] == "after restart"
print("restart: run survived a full restart and resumed from its checkpoint")

# 12. a run that was RUNNING when the server died is marked FAILED at the next startup
with psycopg.connect(os.environ["DATABASE_URL"], autocommit=True) as conn:
    conn.execute(
        "INSERT INTO runs (run_id, status) VALUES ('run_stale_test', 'RUNNING')"
        " ON CONFLICT (run_id) DO UPDATE SET status = 'RUNNING', error = NULL"
    )
with TestClient(app) as c3:
    stale = c3.get("/v1/runs/run_stale_test").json()
    assert stale["status"] == "FAILED" and "restart" in stale["error"], stale
print("stale  :", stale["status"], "|", stale["error"])


# 13. ten reviewers press Approve at the same instant: the database lets exactly one win
async def race():
    pool = AsyncConnectionPool(
        os.environ["DATABASE_URL"], min_size=1, max_size=12, open=False,
        kwargs={"autocommit": True, "row_factory": dict_row},
    )
    await pool.open(wait=True, timeout=10)
    try:
        race_id = f"run_race_{int(time.time() * 1000)}"
        await db.insert_run(pool, race_id)
        await db.mark_waiting(pool, race_id, {"answer": "x"})
        return await asyncio.gather(*[db.claim_for_review(pool, race_id) for _ in range(10)])
    finally:
        await pool.close()


outcomes = asyncio.run(race())
assert outcomes.count("claimed") == 1 and outcomes.count("RUNNING") == 9, outcomes
print("race   : 10 simultaneous reviews ->", outcomes.count("claimed"), "claimed,", outcomes.count("RUNNING"), "told it is already RUNNING")

# ---- graph-level checks (no HTTP): judge pass path and the node retry policy
import graph as g
from langgraph.checkpoint.memory import MemorySaver
from langgraph.types import Command


def run_graph(**extra):
    gr = g.build_graph(MemorySaver())
    cfg = {"configurable": {"thread_id": "t"}, "recursion_limit": 30}
    state = {"question": "q", "retries": 0, "tokens_used": 0, "token_budget": 150_000, **extra}
    return gr.invoke(state, cfg)


# 9. the judge CAN pass when allowed to, and then stop_reason is "pass"
res = run_graph(judge_pass_at=2)
payload = res["__interrupt__"][0].value
print("pass   :", payload["verdict"], payload["retries"], payload["stop_reason"])
assert (payload["verdict"], payload["retries"], payload["stop_reason"]) == ("pass", 2, "pass")

# 10. a transient error (timeout) is retried by the node policy; a bug (ValueError) is not
real_draft, calls = g.draft, {"n": 0}


def flaky_once(state):
    calls["n"] += 1
    if calls["n"] == 1:
        raise TimeoutError("provider timed out")
    return real_draft(state)


g.draft = flaky_once
try:
    run_graph(judge_pass_at=1)
    assert calls["n"] == 2, calls          # failed once, retried once
    calls["n"] = 0

    def buggy(state):
        calls["n"] += 1
        raise ValueError("bug")

    g.draft = buggy
    try:
        run_graph()
        raise AssertionError("ValueError should have propagated")
    except ValueError:
        assert calls["n"] == 1, calls      # not retried
finally:
    g.draft = real_draft
print("retry  : timeout retried once, ValueError not retried")

print("ALL CHECKS PASSED")
