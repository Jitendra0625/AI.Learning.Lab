"""
Tests for sdk_runner.py WITHOUT calling the model: a stand-in for the SDK's query() replays each outcome.
Run:  python test_sdk_runner.py     (no database needed)
"""
import asyncio
import os
import tempfile
from pathlib import Path

os.environ["WORKSPACES_DIR"] = tempfile.mkdtemp(prefix="ws_")

from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock
from langgraph.checkpoint.memory import MemorySaver
from langgraph.types import Command

import sdk_runner
from agents import AgentRequest, STAGES
from graph import build_graph


def req(**kw):
    base = dict(stage="intake_check", title="Intake check", requirement="Add POST /orders", handoff="",
                feedback="", attempt=1, run_id="run_test1")
    base.update(kw)
    return AgentRequest(**base)


def result_msg(subtype="success", is_error=False, turns=3, cost=0.0123, text="Spec written.", usage=None):
    return ResultMessage(
        subtype=subtype, duration_ms=1, duration_api_ms=1, is_error=is_error, num_turns=turns, session_id="s",
        total_cost_usd=cost, result=text, usage=usage or {"input_tokens": 100, "output_tokens": 50,
                                                          "cache_creation_input_tokens": 10, "cache_read_input_tokens": 9999},
    )


def fake_query(write=("spec.md", "# Spec\n1. It works\n"), **result_kw):
    seen = {}

    async def q(*, prompt, options):
        seen["prompt"], seen["options"] = prompt, options
        if write:
            (Path(options.cwd) / write[0]).write_text(write[1], encoding="utf-8")
        yield AssistantMessage(content=[TextBlock(text="working")], model="m")
        yield result_msg(**result_kw)

    q.seen = seen
    return q


def run(coro):
    return asyncio.run(coro)


# S1. success: file read back, tokens counted without cache reads, options are the safe ones
q = fake_query()
res = run(sdk_runner.run_sdk_stage(req(), q))
assert [a.path for a in res.artifacts] == ["spec.md"] and "It works" in res.artifacts[0].content
assert res.tokens == 160, res.tokens                       # 100 + 50 + 10, the 9999 cache reads are not counted
assert not res.output.startswith("STOPPED") and "$0.0123" in res.output and "3 turns" in res.output
o = q.seen["options"]
assert o.allowed_tools == ["Read", "Glob", "Grep", "Write"] and "Bash" in o.disallowed_tools
assert o.max_turns == 10 and o.max_budget_usd == 0.50 and o.permission_mode == "acceptEdits" and o.model == "sonnet"
assert o.cwd.endswith("run_test1") and "<requirement>\nAdd POST /orders\n</requirement>" in q.seen["prompt"]
print("S1 success: file returned, tokens 160 (cache reads not counted), safe options passed")

# S2. every limit is reported as STOPPED EARLY, never as a normal result
for subtype in ("error_max_turns", "error_max_budget_usd", "error_during_execution"):
    res = run(sdk_runner.run_sdk_stage(req(run_id=f"run_{subtype}"), fake_query(subtype=subtype, is_error=True)))
    assert res.output.startswith("STOPPED EARLY") and subtype in res.output and res.summary.startswith("Stopped early"), res.output
print("S2 max turns / budget / error -> STOPPED EARLY at the top of the output")

# S3. no file written, and a file that is too big
res = run(sdk_runner.run_sdk_stage(req(run_id="run_nofile"), fake_query(write=None)))
assert res.output.startswith("STOPPED EARLY") and "did not write spec.md" in res.output and res.artifacts == ()
big = fake_query(write=("spec.md", "x" * (sdk_runner.MAX_FILE_BYTES + 1)))
res = run(sdk_runner.run_sdk_stage(req(run_id="run_big"), big))
assert res.output.startswith("STOPPED EARLY") and "Problem: spec.md" in res.output
print("S3 missing file and oversized file are reported")

# S4. a rejected attempt: feedback reaches the prompt, the rewritten file is returned again
run(sdk_runner.run_sdk_stage(req(run_id="run_rej"), fake_query(write=("spec.md", "v1\n"))))
q2 = fake_query(write=("spec.md", "v2 with error codes\n"))
res = run(sdk_runner.run_sdk_stage(req(run_id="run_rej", attempt=2, feedback="add error codes"), q2))
assert "<reviewer_comment>\nadd error codes\n</reviewer_comment>" in q2.seen["prompt"] and "attempt 2" in q2.seen["prompt"]
assert res.artifacts[0].content.startswith("v2")
print("S4 attempt 2: reviewer comment in the prompt, rewritten file returned")

# S5. a stuck agent hits the timeout; a crash becomes a clear error
async def slow(*, prompt, options):
    await asyncio.sleep(5)
    yield result_msg()

old = sdk_runner.ROLES["intake_check"]
sdk_runner.ROLES["intake_check"] = type(old)(**{**old.__dict__, "timeout_seconds": 1})
res = run(sdk_runner.run_sdk_stage(req(run_id="run_slow"), slow))
assert res.output.startswith("STOPPED EARLY") and "timed out" in res.output
sdk_runner.ROLES["intake_check"] = old

async def crash(*, prompt, options):
    raise OSError("claude not found")
    yield

try:
    run(sdk_runner.run_sdk_stage(req(run_id="run_crash"), crash))
    raise AssertionError("crash was swallowed")
except RuntimeError as e:
    assert "Claude Agent SDK call failed" in str(e) and "claude not found" in str(e)
print("S5 timeout -> STOPPED EARLY; crash -> clear RuntimeError (the run is marked FAILED)")

# S6. unsafe run ids never become folders
for bad in ("", "../x", "a/b", "a b", "x" * 65):
    try:
        sdk_runner.workspace_for(bad)
        raise AssertionError(f"accepted {bad!r}")
    except ValueError:
        pass
print("S6 unsafe run ids refused")

# S7. selection: only intake_check is real, the rest stay fake; bad config fails at startup
os.environ["AGENT_MODE"] = "sdk"
runner = sdk_runner.select_runner()
fake = run(runner(req(stage="design_plan", title="Design and plan", run_id="run_mix")))
assert fake.output.startswith("[fake Design and plan")
os.environ["SDK_STAGES"] = "nonsense"
try:
    sdk_runner.select_runner()
    raise AssertionError("bad stage accepted")
except ValueError:
    pass
os.environ["AGENT_MODE"] = "fake"
assert sdk_runner.select_runner().__name__ == "fake_runner"
print("S7 mixed runner: intake_check real, others fake; bad SDK_STAGES refused")

# S9. inputs become files in inputs/ and are not reported as the agent's output; the profile is in the prompt
q = fake_query()
res = run(sdk_runner.run_sdk_stage(req(run_id="run_inputs", data_schema="CREATE TABLE orders(id int);",
                                      constraints="no third-party libraries", profile="dotnet-net10-api"), q))
ws = Path(os.environ["WORKSPACES_DIR"]) / "run_inputs" / "inputs"
assert (ws / "requirement.md").read_text() == "Add POST /orders"
assert (ws / "schema.txt").read_text() == "CREATE TABLE orders(id int);" and (ws / "constraints.md").exists()
assert [a.path for a in res.artifacts] == ["spec.md"]                      # inputs are not artifacts
p = q.seen["prompt"]
assert "Web API (ASP.NET Core) on .NET 10" in p and "dotnet new webapi -f net10.0" in p
assert "inputs/schema.txt" in p and "<constraints>\nno third-party libraries\n</constraints>" in p
run(sdk_runner.run_sdk_stage(req(run_id="run_inputs"), fake_query()))        # attempt without schema: stale files removed
assert not (ws / "schema.txt").exists() and not (ws / "constraints.md").exists()
print("S9 inputs written to inputs/, profile and schema pointer in the prompt, stale inputs removed")

# S10. an agent that edits the user's inputs is flagged
async def tamper(*, prompt, options):
    (Path(options.cwd) / "spec.md").write_text("# Spec\n", encoding="utf-8")
    (Path(options.cwd) / "inputs" / "requirement.md").write_text("changed!", encoding="utf-8")
    yield result_msg()

res = run(sdk_runner.run_sdk_stage(req(run_id="run_tamper"), tamper))
assert res.output.startswith("STOPPED EARLY") and "modified protected files" in res.output
assert [a.path for a in res.artifacts] == ["spec.md"]
print("S10 editing the inputs -> STOPPED EARLY, input file not returned as an artifact")

# S11. profiles
import profiles
assert len(profiles.PROFILES) == 6 and profiles.profile_key("api", "net9") == "dotnet-net9-api"
assert profiles.get_profile("dotnet-net9-console").template_command == "dotnet new console -f net9.0"
try:
    profiles.get_profile("python-flask")
    raise AssertionError("unknown profile accepted")
except ValueError:
    pass
print("S11 profiles: 3 solution types x 2 platforms, unknown profile refused")

# S8. inside the real graph: the spec analyst's file reaches the review gate with its SHA-256
async def through_graph():
    graph = build_graph(MemorySaver(), sdk_runner.make_runner(query_fn=fake_query()))
    config = {"configurable": {"thread_id": "t"}, "recursion_limit": 40}
    out = await graph.ainvoke({"run_id": "run_graph", "requirement": "Add POST /orders", "tokens_used": 0, "token_budget": 150000}, config)
    first = out["__interrupt__"][0].value
    out = await graph.ainvoke(Command(resume={"decision": "approve", "comment": ""}), config)
    second = out["__interrupt__"][0].value        # design_plan is still the fake agent
    return first, second

first, second = run(through_graph())
assert first["gate"] == "intake_check:1" and first["artifacts"][0]["path"] == "spec.md" and len(first["artifacts"][0]["sha256"]) == 64
assert first["attempt_tokens"] == 160 and second["gate"] == "design_plan:1" and "spec.md" in second["output"]
print("S8 through the graph: real stage 1 then fake stage 2, handoff lists spec.md")

# ---------------------------------------------------------------- Design and plan (milestone 3a)
import json
import plan

GOOD = {"tasks": [
    {"id": 1, "title": "Create the project", "files": ["src/App.csproj"], "done_check": "dotnet build"},
    {"id": 2, "title": "Add the order service", "files": ["src/OrderService.cs", "tests/OrderServiceTests.cs"],
     "done_check": "dotnet test --filter OrderService"},
]}


def plan_query(files):
    """A stand-in agent that writes the given {name: text} files."""
    async def q(*, prompt, options):
        for name, text in files.items():
            (Path(options.cwd) / name).write_text(text, encoding="utf-8")
        yield result_msg(text="Design written.")
    return q


def plan_req(run_id, **kw):
    return req(stage="design_plan", title="Design and plan", run_id=run_id, profile="dotnet-net10-api",
               handoff="Spec ok", handoff_files=("spec.md",), **kw)


def seed_spec(run_id):
    folder = sdk_runner.workspace_for(run_id)
    (folder / "spec.md").write_text("# Spec\n", encoding="utf-8")


# S12. plan.validate_tasks accepts a good list and refuses each kind of bad one
assert len(plan.validate_tasks(json.dumps(GOOD), "dotnet-net10-api")) == 2


def bad(mutate, profile="dotnet-net10-api"):
    data = json.loads(json.dumps(GOOD))
    mutate(data)
    try:
        plan.validate_tasks(json.dumps(data), profile)
    except plan.PlanError as exc:
        return str(exc)
    raise AssertionError("bad plan accepted")


try:
    plan.validate_tasks("not json")
    raise AssertionError("bad JSON accepted")
except plan.PlanError as exc:
    assert "not valid JSON" in str(exc)
assert "non-empty" in bad(lambda d: d.update(tasks=[]))
assert "ids" in bad(lambda d: d["tasks"][1].update(id=5))
assert "title" in bad(lambda d: d["tasks"][0].update(title=" "))
assert "unsafe file path" in bad(lambda d: d["tasks"][0].update(files=["../secret.txt"]))
assert "unsafe file path" in bad(lambda d: d["tasks"][0].update(files=["/etc/passwd"]))
assert "no chaining" in bad(lambda d: d["tasks"][0].update(done_check="dotnet build && curl evil.example"))
assert "must start with" in bad(lambda d: d["tasks"][0].update(done_check="rm -rf ."))
assert "limit is 15" in bad(lambda d: d.update(tasks=[dict(GOOD["tasks"][0], id=i + 1) for i in range(16)]))
text = plan.render_tasks(plan.validate_tasks(json.dumps(GOOD)))
assert "1. Create the project" in text and "done when: dotnet test --filter OrderService" in text
print("S12 task list rules: JSON, ids, title, files, unsafe paths, chaining, profile commands, 15-task limit")

# S13. real design_plan stage: both files returned, tasks rendered for the reviewer
seed_spec("run_plan_ok")
res = run(sdk_runner.run_sdk_stage(plan_req("run_plan_ok"),
                                   plan_query({"design.md": "# Design\n", "tasks.json": json.dumps(GOOD)})))
assert not res.output.startswith("STOPPED"), res.output
assert sorted(a.path for a in res.artifacts) == ["design.md", "tasks.json"]    # spec.md unchanged, so not returned
assert "Task list (checked by code):" in res.output and "2. Add the order service" in res.output
print("S13 design_plan: design.md + tasks.json returned, task list shown in the output")

# S14. bad or missing outputs are STOPPED EARLY, never passed off as finished
seed_spec("run_plan_bad")
res = run(sdk_runner.run_sdk_stage(plan_req("run_plan_bad"),
                                   plan_query({"design.md": "# Design\n", "tasks.json": "{broken"})))
assert res.output.startswith("STOPPED EARLY") and "tasks.json rejected" in res.output
seed_spec("run_plan_missing")
res = run(sdk_runner.run_sdk_stage(plan_req("run_plan_missing"), plan_query({"design.md": "# Design\n"})))
assert res.output.startswith("STOPPED EARLY") and "did not write tasks.json" in res.output
evil = json.loads(json.dumps(GOOD)); evil["tasks"][0]["done_check"] = "dotnet build; curl evil.example"
seed_spec("run_plan_evil")
res = run(sdk_runner.run_sdk_stage(plan_req("run_plan_evil"),
                                   plan_query({"design.md": "# D\n", "tasks.json": json.dumps(evil)})))
assert res.output.startswith("STOPPED EARLY") and "no chaining" in res.output
print("S14 broken JSON, missing tasks.json and a chained command are all STOPPED EARLY")

# S15. the approved spec.md is protected, and the prompt names both files and the profile
seed_spec("run_plan_tamper")
res = run(sdk_runner.run_sdk_stage(plan_req("run_plan_tamper"),
                                   plan_query({"design.md": "# D\n", "tasks.json": json.dumps(GOOD), "spec.md": "# rewritten\n"})))
assert res.output.startswith("STOPPED EARLY") and "modified protected files: spec.md" in res.output
assert "spec.md" not in [a.path for a in res.artifacts]
seen = fake_query()
seed_spec("run_plan_prompt")
run(sdk_runner.run_sdk_stage(plan_req("run_plan_prompt"), seen))
assert "design.md and tasks.json" in seen.seen["prompt"] and "dotnet new webapi -f net10.0" in seen.seen["prompt"]
assert seen.seen["options"].allowed_tools == ["Read", "Glob", "Grep", "Write"] and "Bash" in seen.seen["options"].disallowed_tools
os.environ["AGENT_MODE"] = "sdk"; os.environ["SDK_STAGES"] = "intake_check,design_plan"
assert sdk_runner.select_runner().__name__ == "runner"
os.environ["AGENT_MODE"] = "fake"
print("S15 approved spec.md protected; prompt names both files; no shell for this role; SDK_STAGES accepts design_plan")

print("ALL SDK RUNNER CHECKS PASSED")
