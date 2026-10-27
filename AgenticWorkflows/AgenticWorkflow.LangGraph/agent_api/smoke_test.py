"""
End-to-end checks: python smoke_test.py

Part A tests the pipeline graph on its own (no database, no HTTP): gates, reject loops, limits,
handoffs, retries. Part B tests the real API against Postgres, which must be running:
    docker compose up -d       (or set DATABASE_URL to another database)

Two tracebacks in the output ("smtp down", "boom") are deliberate failure tests.
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
from langgraph.checkpoint.memory import MemorySaver
from langgraph.types import Command

import agents
import graph as g
import notifier
from agents import FAKE_TOKENS, STAGES, AgentResult, fake_runner
from app import RECURSION_LIMIT, app

KEYS = [s.key for s in STAGES]


# ======================================================================================
# Part A: the pipeline graph on its own
# ======================================================================================

class Recorder:
    """Wraps a runner and remembers every request it received."""

    def __init__(self, inner=fake_runner):
        self.inner, self.calls = inner, []

    async def __call__(self, req):
        self.calls.append(req)
        return await self.inner(req)

    def for_stage(self, key):
        return [c for c in self.calls if c.stage == key]


async def run_pipeline(script, runner=None, budget=150_000, thread="t"):
    """Start the graph and answer every review with script(payload) -> (decision, comment)."""
    graph = g.build_graph(MemorySaver(), runner or fake_runner)
    cfg = {"configurable": {"thread_id": thread}, "recursion_limit": RECURSION_LIMIT}
    result = await graph.ainvoke({"requirement": "REQ", "tokens_used": 0, "token_budget": budget}, cfg)
    gates = []
    while "__interrupt__" in result:
        payload = result["__interrupt__"][0].value
        gates.append(payload)
        decision, comment = script(payload)
        result = await graph.ainvoke(Command(resume={"decision": decision, "comment": comment}), cfg)
    return result, gates


def approve_all(p):
    return "approve", ""


def part_a():
    # A1. every stage approved: five gates in order, nothing rejected
    res, gates = asyncio.run(run_pipeline(approve_all))
    assert [p["stage"] for p in gates] == KEYS
    assert [p["step"] for p in gates] == [1, 2, 3, 4, 5] and all(p["total_steps"] == 5 for p in gates)
    assert [p["gate"] for p in gates] == [f"{k}:1" for k in KEYS]
    assert (res["outcome"], res["stop_reason"], res["stopped_at"]) == ("APPROVED", "all_stages_approved", "")
    assert res["tokens_used"] == sum(FAKE_TOKENS.values())
    assert set(res["approved"]) == set(KEYS) and len(res["history"]) == 5
    print("A1 all five stages approved in order; tokens", res["tokens_used"])

    # A2. reject one stage: ONLY that agent runs again, with the comment; nothing else repeats
    rec = Recorder()

    def reject_designer_once(p):
        if p["stage"] == "designer" and p["attempt"] == 1:
            return "reject", "add error codes"
        return "approve", ""

    res, gates = asyncio.run(run_pipeline(reject_designer_once, runner=rec))
    assert [p["stage"] for p in gates] == ["spec_analyst", "designer", "designer", "implementer", "test_engineer", "reviewer"]
    assert gates[2]["attempt"] == 2 and gates[2]["gate"] == "designer:2" and gates[2]["previous_feedback"] == "add error codes"
    assert len(rec.for_stage("spec_analyst")) == 1 and len(rec.for_stage("designer")) == 2
    assert [c.feedback for c in rec.for_stage("designer")] == ["", "add error codes"]
    assert rec.for_stage("implementer")[0].feedback == ""          # cleared once the designer was approved
    assert res["outcome"] == "APPROVED" and res["attempts"]["designer"] == 2
    assert [h["decision"] for h in res["history"]] == ["approve", "reject", "approve", "approve", "approve", "approve"]
    print("A2 reject -> same agent reruns with the comment; other stages untouched")

    # A3. a stage rejected too often stops the run and names the stage
    rec = Recorder()
    res, gates = asyncio.run(run_pipeline(
        lambda p: ("reject", f"still wrong {p['attempt']}") if p["stage"] == "implementer" else ("approve", ""), runner=rec))
    assert (res["outcome"], res["stop_reason"], res["stopped_at"]) == ("REJECTED", "max_rejects", "implementer")
    assert res["attempts"]["implementer"] == g.MAX_ATTEMPTS_PER_STAGE
    assert not rec.for_stage("test_engineer") and set(res["approved"]) == {"spec_analyst", "designer"}
    print("A3 rejected", g.MAX_ATTEMPTS_PER_STAGE, "times -> REJECTED at", res["stopped_at"])

    # A4. token budget stops the run between stages
    rec = Recorder()
    res, _ = asyncio.run(run_pipeline(approve_all, runner=rec, budget=2000))
    assert (res["outcome"], res["stop_reason"], res["stopped_at"]) == ("STOPPED_BUDGET", "budget", "designer")
    assert not rec.for_stage("implementer") and res["tokens_used"] == 900 + 1300
    res, _ = asyncio.run(run_pipeline(lambda p: ("reject", "no"), budget=1000))   # budget also ends reject loops
    assert res["outcome"] == "STOPPED_BUDGET" and res["attempts"]["spec_analyst"] == 2   # stopped before the 3rd try
    print("A4 budget stops the run (after approve and in a reject loop)")

    # A5. handoff = the previous stage's SHORT summary, nothing else
    async def chatty(req):
        r = await fake_runner(req)
        return AgentResult(r.output, f"{req.stage}-SUMMARY " + "x" * 5000, r.tokens)

    rec = Recorder(chatty)
    asyncio.run(run_pipeline(approve_all, runner=rec))
    assert rec.calls[0].handoff == ""
    for i in range(1, 5):
        h = rec.calls[i].handoff
        assert h.startswith(f"{KEYS[i - 1]}-SUMMARY") and len(h) <= g.MAX_SUMMARY_CHARS
        if i >= 2:
            assert KEYS[i - 2] not in h                      # nothing from two stages back
    assert all(not hasattr(c, "outputs") for c in rec.calls)  # full outputs are never passed
    print("A5 handoff is only the previous summary, capped at", g.MAX_SUMMARY_CHARS, "chars")

    # A6. a bad decision value is an error, not a silent approval
    async def bad_decision():
        graph = g.build_graph(MemorySaver())
        cfg = {"configurable": {"thread_id": "bad"}, "recursion_limit": RECURSION_LIMIT}
        await graph.ainvoke({"requirement": "R", "tokens_used": 0, "token_budget": 1000}, cfg)
        await graph.ainvoke(Command(resume={"decision": "maybe"}), cfg)

    try:
        asyncio.run(bad_decision())
        raise AssertionError("expected ValueError")
    except ValueError:
        pass
    print("A6 invalid decision rejected")

    # A7. transient errors are retried by the node policy; a bug is not
    state = {"n": 0}

    async def flaky(req):
        if req.stage == "designer":
            state["n"] += 1
            if state["n"] == 1:
                raise TimeoutError("provider timed out")
        return await fake_runner(req)

    rec = Recorder(flaky)
    res, gates = asyncio.run(run_pipeline(approve_all, runner=rec))
    assert state["n"] == 2 and res["outcome"] == "APPROVED"
    assert res["attempts"]["designer"] == 1 and gates[1]["attempt"] == 1   # a retry is not a new attempt

    async def buggy(req):
        state["bug"] = state.get("bug", 0) + 1
        raise ValueError("bug")

    try:
        asyncio.run(run_pipeline(approve_all, runner=buggy))
        raise AssertionError("ValueError should have propagated")
    except ValueError:
        assert state["bug"] == 1
    print("A7 timeout retried once (not counted as an attempt); ValueError not retried")

    # A8. the worst legitimate run fits inside the recursion limit
    rec = Recorder()
    res, gates = asyncio.run(run_pipeline(lambda p: ("reject", "again") if p["attempt"] < 3 else ("approve", ""), runner=rec))
    assert res["outcome"] == "APPROVED" and len(rec.calls) == 15 and len(gates) == 15
    assert RECURSION_LIMIT >= 2 * len(STAGES) * g.MAX_ATTEMPTS_PER_STAGE + 1
    print("A8 worst case (every stage rejected twice): 15 agent runs, 15 reviews, limit", RECURSION_LIMIT)

    # A9. the diagram can be drawn
    diagram = g.build_graph(MemorySaver()).get_graph().draw_mermaid()
    assert all(k in diagram for k in KEYS) and "finish" in diagram and "retry" in diagram
    print("A9 mermaid diagram drawn")


part_a()
print("--- part A passed ---\n")

# ======================================================================================
# Part B: the API against Postgres
# ======================================================================================

sent = []
notifier.send_email = lambda to, subject, body: sent.append((to, subject, body))   # capture, do not send


def wait_until(condition, timeout=5.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if condition():
            return
        time.sleep(0.05)
    raise AssertionError("condition not reached in time")


def wait_for(client, run_id, wanted, timeout=8.0):
    deadline = time.time() + timeout
    body = {}
    while time.time() < deadline:
        body = client.get(f"/v1/runs/{run_id}").json()
        if body["status"] in wanted:
            return body
        time.sleep(0.05)
    raise AssertionError(f"timed out waiting for {wanted}, last status {body.get('status')}")


def next_gate(client, run_id, after=None):
    """Wait for a review gate different from `after`, or for the run to end. Returns the run body."""
    deadline = time.time() + 8
    body = {}
    while time.time() < deadline:
        body = client.get(f"/v1/runs/{run_id}").json()
        if body["status"] in ("COMPLETED", "FAILED"):
            return body
        if body["status"] == "WAITING_REVIEW" and body["pending_review"]["gate"] != after:
            return body
        time.sleep(0.05)
    raise AssertionError(f"no new gate after {after}, last {body.get('status')}")


def answer(client, run_id, body, decision, comment=""):
    return client.post(f"/v1/runs/{run_id}/review", json={"gate": body["pending_review"]["gate"], "decision": decision, "comment": comment})


def drive(client, run_id, script=lambda p: ("approve", ""), first=None):
    """Answer every review until the run ends. Returns (final body, list of pending payloads seen)."""
    body = first or next_gate(client, run_id)
    seen = []
    while body["status"] == "WAITING_REVIEW":
        p = body["pending_review"]
        seen.append(p)
        decision, comment = script(p)
        assert answer(client, run_id, body, decision, comment).status_code == 202
        body = next_gate(client, run_id, after=p["gate"])
    return body, seen


def start(client, requirement="Add POST /orders", **extra):
    r = client.post("/v1/runs", json={"requirement": requirement, **extra})
    assert r.status_code == 202, r.text
    return r.json()["run_id"]


with TestClient(app) as client:   # the `with` keeps the app (and its background tasks) alive
    # B1. input validation and unknown ids
    assert client.post("/v1/runs", json={}).status_code == 422
    assert client.post("/v1/runs", json={"question": "old field name"}).status_code == 422
    assert client.post("/v1/runs", json={"requirement": ""}).status_code == 422
    assert client.post("/v1/runs", json={"requirement": "x", "token_budget": 0}).status_code == 422
    assert client.get("/v1/runs/run_missing").status_code == 404
    assert client.post("/v1/runs/run_missing/review", json={"gate": "a:1", "decision": "approve"}).status_code == 404
    print("B1 validation: 422 for bad input, 404 for unknown runs")

    # B2. a whole run over HTTP, five reviews, then a late review of a finished run
    run_id = start(client)
    first = next_gate(client, run_id)
    assert first["status"] == "WAITING_REVIEW" and first["next_node"] == ["spec_analyst_review"]
    assert first["pending_review"]["step"] == 1 and first["pending_review"]["total_steps"] == 5
    r1 = answer(client, run_id, first, "approve", "looks right")
    r2 = answer(client, run_id, first, "approve")           # the same click again (same gate)
    assert (r1.status_code, r2.status_code) == (202, 409), (r1.text, r2.text)
    body, seen = drive(client, run_id, first=next_gate(client, run_id, after=first["pending_review"]["gate"]))
    assert [p["stage"] for p in seen] == KEYS[1:]
    assert body["status"] == "COMPLETED" and body["outcome"] == "APPROVED"
    assert [h["decision"] for h in body["result"]["history"]] == ["approve"] * 5
    assert body["result"]["history"][0]["comment"] == "looks right"
    assert body["state"]["tokens_used"] == sum(FAKE_TOKENS.values()) and body["next_node"] == []
    assert answer(client, run_id, first, "approve").status_code == 409
    print("B2 full run: 5 reviews, double click -> 409, finished run -> 409; outcome", body["outcome"])

    # B3. reject over HTTP: the same stage comes back with the comment
    run_id = start(client)
    seen_state = {}

    def reject_designer_once(p):
        if p["stage"] == "designer" and p["attempt"] == 1:
            return "reject", "add error codes"
        return "approve", ""

    body, seen = drive(client, run_id, reject_designer_once)
    again = [p for p in seen if p["gate"] == "designer:2"][0]
    assert again["previous_feedback"] == "add error codes" and "add error codes" in again["output"]
    assert body["outcome"] == "APPROVED" and body["state"]["attempts"]["designer"] == 2
    print("B3 reject -> designer:2 reviewed with the comment, run still APPROVED")

    # B4. rejected too often, and token budget, over HTTP
    run_id = start(client)
    body, seen = drive(client, run_id, lambda p: ("reject", "no good"))
    assert body["status"] == "COMPLETED" and body["outcome"] == "REJECTED"
    assert body["result"]["stop_reason"] == "max_rejects" and body["result"]["stopped_at"] == "spec_analyst"
    assert [p["gate"] for p in seen] == ["spec_analyst:1", "spec_analyst:2", "spec_analyst:3"]
    run_b = start(client, token_budget=2000)
    body, seen = drive(client, run_b)
    assert body["outcome"] == "STOPPED_BUDGET" and body["result"]["stopped_at"] == "designer" and len(seen) == 2
    print("B4 max rejects -> REJECTED; token budget -> STOPPED_BUDGET")

    # B5. stale reviews cannot touch a later review (the multi-gate danger)
    run_id = start(client)
    g1 = next_gate(client, run_id)
    assert answer(client, run_id, g1, "approve").status_code == 202
    g2 = next_gate(client, run_id, after=g1["pending_review"]["gate"])
    assert g2["pending_review"]["gate"] == "designer:1"
    late = answer(client, run_id, g1, "approve")             # a late repeat of the FIRST review
    assert late.status_code == 409 and "no longer current" in late.json()["detail"], late.text
    still = client.get(f"/v1/runs/{run_id}").json()
    assert still["status"] == "WAITING_REVIEW" and still["pending_review"]["gate"] == "designer:1"
    print("B5 a late click on review 1 cannot approve review 2")
    drive(client, run_id, first=g2)

    # B6. review by email link: mail -> page -> button -> next stage; old links stay dead
    # The requirement is hostile on purpose: it flows into the agent output, which goes into the page.
    mails = len(sent)
    run_e = start(client, requirement="<script>alert(1)</script>")
    wait_until(lambda: len(sent) > mails)
    to, subject, mail_body = sent[-1]
    assert to == "reviewer@example.com" and run_e in subject and "step 1/5" in subject
    path1 = re.search(r"/review/\S+", mail_body).group(0)

    page = client.get(path1)
    assert page.status_code == 200 and "Approve" in page.text and "Reject" in page.text and "Spec analyst" in page.text
    assert "Step 1 of 5" in page.text
    assert "<script>alert" not in page.text and "&lt;script&gt;" in page.text   # model/user text is escaped
    assert client.get(f"/v1/runs/{run_e}").json()["pending_review"]["gate"] == "spec_analyst:1"   # opening it changed nothing

    assert client.get(path1[:-3] + "abc").status_code == 400                       # tampered link
    expired = "/review/" + notifier.make_token(run_e, "spec_analyst:1", ttl=-10)
    assert client.get(expired).status_code == 400 and client.post(expired, data={"decision": "approve"}).status_code == 400
    assert client.post(path1, data={"decision": "maybe"}).status_code == 400       # bad form value

    mails = len(sent)
    ok = client.post(path1, data={"decision": "approve", "comment": "via email"})
    assert ok.status_code == 200 and "Approved" in ok.text
    wait_until(lambda: len(sent) > mails)                                          # a NEW mail for review 2
    assert "step 2/5" in sent[-1][1]
    path2 = re.search(r"/review/\S+", sent[-1][2]).group(0)
    assert path2 != path1

    old_get, old_post = client.get(path1), client.post(path1, data={"decision": "approve"})
    assert old_get.status_code == 409 and "earlier review" in old_get.text          # the stale link is refused
    assert old_post.status_code == 409
    assert client.get(f"/v1/runs/{run_e}").json()["pending_review"]["gate"] == "designer:1"   # review 2 untouched
    assert client.post(path2, data={"decision": "reject", "comment": "needs <b>more</b>"}).status_code == 200
    wait_until(lambda: len(sent) > mails + 1)                                      # designer:2 mail, shows the comment
    assert "needs <b>more</b>" in sent[-1][2] and "attempt 2/3" in sent[-1][1]
    page3 = client.get(re.search(r"/review/\S+", sent[-1][2]).group(0))
    assert "needs &lt;b&gt;more&lt;/b&gt;" in page3.text and "needs <b>more</b>" not in page3.text
    body, _ = drive(client, run_e)
    assert body["outcome"] == "APPROVED"
    assert client.get(path2).status_code == 409                                    # finished run: nothing to review
    print("B6 email flow: page escaped, tampered/expired/old links refused, one mail per review")

    # a mail failure must not fail the run
    def broken_mail(to, subject, body):
        raise OSError("smtp down")

    notifier.send_email = broken_mail
    run_m = start(client, requirement="mail will fail")
    time.sleep(0.4)
    assert client.get(f"/v1/runs/{run_m}").json()["status"] == "WAITING_REVIEW"
    notifier.send_email = lambda to, subject, body: sent.append((to, subject, body))
    print("B7 mail failure leaves the run waiting")

    # an exception inside the graph is recorded as FAILED, not swallowed
    class BrokenGraph:
        async def ainvoke(self, *args, **kwargs):
            raise RuntimeError("boom")

        async def aget_state(self, config):
            class Snap:
                values, next = {}, ()
            return Snap()

    real_graph = app.state.graph
    app.state.graph = BrokenGraph()
    run3 = start(client, requirement="this will fail")
    failed = wait_for(client, run3, {"FAILED"})
    assert "boom" in failed["error"]
    app.state.graph = real_graph
    print("B8 graph exception recorded:", failed["status"], "|", failed["error"])

# ---- persistence checks: this is what Postgres buys you
import psycopg
from psycopg.rows import dict_row
from psycopg_pool import AsyncConnectionPool

import db

# B9. restart survival in the MIDDLE of the pipeline: stage 1 approved, stage 2 waiting, app restarted
with TestClient(app) as c1:
    rid = start(c1, requirement="survive a restart")
    a = next_gate(c1, rid)
    assert answer(c1, rid, a, "approve").status_code == 202
    b = next_gate(c1, rid, after=a["pending_review"]["gate"])
    assert b["pending_review"]["gate"] == "designer:1"
    time.sleep(0.3)   # let the mail step finish before shutdown
# the with-block ended: the app shut down and everything held in memory is gone
with TestClient(app) as c2:   # a fresh startup: new pool, saver and graph
    after = c2.get(f"/v1/runs/{rid}").json()
    assert after["status"] == "WAITING_REVIEW" and after["next_node"] == ["designer_review"], after
    assert "spec_analyst" in after["state"]["approved"] and after["pending_review"]["gate"] == "designer:1"
    assert answer(c2, rid, after, "approve").status_code == 202    # the same gate still works after the restart
    finished, seen = drive(c2, rid, first=next_gate(c2, rid, after="designer:1"))
    assert finished["outcome"] == "APPROVED" and finished["state"]["attempts"] == {k: 1 for k in KEYS}
print("B9 restart in the middle of the pipeline: stage 1 approved, resumed at stage 2, finished")

# B10. a run that was RUNNING when the server died is marked FAILED at the next startup
with psycopg.connect(os.environ["DATABASE_URL"], autocommit=True) as conn:
    conn.execute(
        "INSERT INTO runs (run_id, status) VALUES ('run_stale_test', 'RUNNING')"
        " ON CONFLICT (run_id) DO UPDATE SET status = 'RUNNING', error = NULL"
    )
with TestClient(app) as c3:
    stale = c3.get("/v1/runs/run_stale_test").json()
    assert stale["status"] == "FAILED" and "restart" in stale["error"], stale
print("B10 stale:", stale["status"], "|", stale["error"])


# B11. ten reviewers press Approve at the same instant: the database lets exactly one win,
#      and a decision naming the wrong gate never wins
async def race():
    pool = AsyncConnectionPool(
        os.environ["DATABASE_URL"], min_size=1, max_size=12, open=False,
        kwargs={"autocommit": True, "row_factory": dict_row},
    )
    await pool.open(wait=True, timeout=10)
    try:
        stamp = int(time.time() * 1000)
        race_id = f"run_race_{stamp}"
        await db.insert_run(pool, race_id)
        await db.mark_waiting(pool, race_id, {"gate": "designer:1"})
        same = await asyncio.gather(*[db.claim_for_review(pool, race_id, "designer:1") for _ in range(10)])
        other_id = f"run_gate_{stamp}"
        await db.insert_run(pool, other_id)
        await db.mark_waiting(pool, other_id, {"gate": "designer:2"})
        wrong = await db.claim_for_review(pool, other_id, "designer:1")
        right = await db.claim_for_review(pool, other_id, "designer:2")
        return same, wrong, right
    finally:
        await pool.close()


same, wrong, right = asyncio.run(race())
assert same.count("claimed") == 1 and same.count("RUNNING") == 9, same
assert (wrong, right) == ("stale_gate", "claimed")
print("B11 race: 10 simultaneous reviews ->", same.count("claimed"), "claimed,", same.count("RUNNING"), "told it is RUNNING; wrong gate ->", wrong)

print("ALL CHECKS PASSED")
