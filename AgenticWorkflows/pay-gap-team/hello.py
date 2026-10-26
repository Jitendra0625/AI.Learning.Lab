import asyncio
from claude_agent_sdk  import query, ClaudeAgentOptions, ResultMessage
from pathlib import Path    
import sys
import json
from gates import build_gate, test_gate
STATE_FILE = Path("work/state.json")
DEVELOPER_RULES = (
    "You are a developer. You write C# on .NET 10 in a minimal API. "
    "You may edit ONLY files under src/PayGapApi. Never edit anything under tests/ and never edit the spec. "
    "Make the existing tests pass by implementing what the spec and plan describe. "
    "Run `dotnet build` and `dotnet test` and keep going until they pass. "
    "Keep changes minimal and add no NuGet packages."
)
DEV_TOOLS = ["Read", "Write", "Edit", "Glob", "Grep",
             "Bash(dotnet build:*)", "Bash(dotnet test:*)"]
def ask_approval(what):
    answer=input(f"\nApprove {what}? Type 'approved' to continue: ")
    if answer.strip().lower() != "approved":
        print(f"Aborting because {what} was not approved.")
        sys.exit(0)
        
def load_state():
    if STATE_FILE.exists():
        return json.loads(STATE_FILE.read_text(encoding="utf-8"))
    return{}
def mark_done(stage):
    state = load_state()
    state[stage] = "approved"
    STATE_FILE.write_text(json.dumps(state), encoding="utf-8")
    
async def run_role(role_name, instructions, task, folder=None, tools=None, budget=0.50):
    options = ClaudeAgentOptions(
        model="sonnet",
        system_prompt=instructions,
        max_budget_usd=0.50,
        cwd=folder,
        allowed_tools=tools or [],
        permission_mode="acceptEdits",
    )
    answer = None
    async for message in query(prompt=task, options=options):
        if isinstance(message, ResultMessage):
            print(f"[{role_name}] cost: ${message.total_cost_usd:.4f}")
            print(f"[{role_name}] status: {message.subtype}, is_error: {message.is_error}")
            if message.is_error or message.result is None:
                print(f"[{role_name}] errors: {message.errors}")
                raise RuntimeError(f"{role_name} failed: {message.subtype}")
            answer = message.result
    return answer
    
def get_verdict(text):
    for line in reversed(text.strip().splitlines()):
        upper = line.strip().upper()
        if upper.startswith("VERDICT:"):
            if "CHANGES_REQUESTED" in upper:
                return "CHANGES_REQUESTED"
            if "APPROVE" in upper:
                return "APPROVE"
    return "UNKNOWN"

async def main():
      # 1. Analyst writes the stories
    if "analyst" in load_state():
        print("Analyst: already approved, skipping.")
    else:
        spec= Path("spec/spec.md").read_text(encoding="utf-8")
        stories = await run_role("analyst", "You are a business analyst. Write short user stories (As a..., I want..., so that...) "
            "with acceptance criteria. Use ONLY the spec you are given. Do not invent features.",
            f"Write the user stories for this spec:\n\n{spec}",)
        Path("work").mkdir(exist_ok=True)
        Path("work/stories.md").write_text(stories, encoding="utf-8")
        print("Saved work/stories.md")
        ask_approval("the stories (read work/stories.md first)")
        mark_done("analyst")
    
    stories = Path("work/stories.md").read_text(encoding="utf-8")

    # 2. Scrum master
    if "scrum_master" in load_state():
        print("Scrum master: already approved, skipping.")
    else:
        spec= Path("spec/spec.md").read_text(encoding="utf-8")
        plan = await run_role(
            "scrum-master",
            "You are a scrum master. Turn user stories into a sprint plan. "
            "Output a table: task id, task, owner (tester or developer), done condition. "
            "Tests are written before code. Keep tasks small. Do not add scope beyond the stories.",
            f"SPEC:\n{spec}\n\nSTORIES:\n{stories}\n\nWrite the sprint plan.",
        )
        Path("work/plan.md").write_text(plan, encoding="utf-8")
        print("Saved work/plan.md")
        ask_approval("the sprint plan (read work/plan.md first)")
        mark_done("scrum_master")
    
     # 3. Tester writes the tests
    plan = Path("work/plan.md").read_text(encoding="utf-8")
    if "tester" in load_state():
        print("Tester: already approved, skipping.")
    else:
        spec= Path("spec/spec.md").read_text(encoding="utf-8")
        report = await run_role(
            "tester",
            "You are a tester. You write xUnit acceptance tests in C# on .NET 10. "
            "You may edit ONLY files under tests/PayGapApi.Tests. Never read or edit anything under src/. "
            "Call the API over HTTP using WebApplicationFactory<Program>. "
            "Start each test name with its rule ID (G1, G2, G3, G4, E1 to E4). "
            "Write each test so it checks the exact values in the spec. "
            "Run `dotnet build` to check your tests compile. Do not implement the endpoint.",
            f"SPEC:\n{spec}\n\nPLAN:\n{plan}\n\n"
            "Do every task in the plan owned by the tester, from T1 up to T11. Skip the close-out task T13. "
            "Finish with a list of the tests you wrote, grouped by rule ID.",
            folder="repo",
            tools=["Read", "Write", "Edit", "Glob", "Grep", "Bash(dotnet build:*)", "Bash(dotnet test:*)"],
            budget=1.50,
        )
        Path("work/tester-report.md").write_text(report, encoding="utf-8")
        print("Saved work/tester-report.md")

        built, _ = build_gate("repo")
        passed, _ = test_gate("repo")
        print(f"Tests compile: {built}. Tests all pass: {passed}.")
        print("We expect: compile True, all pass False (tests are red because there is no endpoint yet).")
        ask_approval("the tests (read repo/tests/PayGapApi.Tests first)")
        mark_done("tester")
    # 4. Developer implements the code (max 3 attempts, counted by our code)
    if "developer" in load_state():
        print("Developer: already approved, skipping.")
    else:
        spec= Path("spec/spec.md").read_text(encoding="utf-8")
        feedback = ""
        passed = False
        attempts_used = 0
        for attempt in range(1, 4):
            attempts_used = attempt
            print(f"--- Developer attempt {attempt} of 3 ---")
            summary = await run_role(
                "developer",
                DEVELOPER_RULES,
                f"SPEC:\n{spec}\n\nPLAN:\n{plan}\n\n"
                "Do every task in the plan owned by the developer. Tests are in tests/PayGapApi.Tests."
                + feedback,
                folder="repo",
                tools=DEV_TOOLS,
                budget=1.50,
            )
            built, build_out = build_gate("repo")
            passed, test_out = test_gate("repo")
            print(f"Attempt {attempt}: build {built}, all tests pass {passed}")
            if built and passed:
                break
            feedback = ("\n\nYour last attempt failed. Here is the end of the test output:\n"
                        + test_out[-3000:])

        Path("work/developer-report.md").write_text(
            f"Attempts used: {attempts_used}\nAll tests pass: {passed}\n\n{summary}", encoding="utf-8")
        print(f"Developer finished. Attempts: {attempts_used}. All tests pass: {passed}.")
        ask_approval("the developer's work (check git diff in repo/ first)")
        mark_done("developer")
    
# 6. Senior reviewer (read-only). Can send work back to the developer, max 2 rework rounds.
    if "reviewer" in load_state():
        print("Reviewer: already approved, skipping.")
    else:
        spec= Path("spec/spec.md").read_text(encoding="utf-8")
        plan= Path("work/plan.md").read_text(encoding="utf-8")
        reviewer_rules = (
            "You are a senior engineer doing a code review. You cannot edit files, only read them. "
            "Review the code under src/ against the spec. Check: every rule in the spec is implemented; "
            "calculations and rounding are correct; validation happens in the agreed order; "
            "behaviour not covered by any test; hard-coded values; clarity. "
            "Cite the file for each finding. Tag each finding BLOCKER (wrong against the spec, or a crash risk) "
            "or MINOR (style or preference). Only BLOCKER findings may request changes. "
            "End with exactly one last line: VERDICT: APPROVE or VERDICT: CHANGES_REQUESTED"
        )
        Path("reports").mkdir(exist_ok=True)
        verdict = "UNKNOWN"
        for round_no in range(1, 4):
            print(f"--- Review round {round_no} ---")
            review = await run_role(
                "reviewer",
                reviewer_rules,
                f"SPEC:\n{spec}\n\nPLAN:\n{plan}\n\nReview the code in this repo against the spec.",
                folder="repo",
                tools=["Read", "Glob", "Grep", "Bash(git diff:*)", "Bash(git status:*)"],
                budget=1.00,
            )
            Path(f"reports/review-round-{round_no}.md").write_text(review, encoding="utf-8")
            verdict = get_verdict(review)
            print(f"Review round {round_no}: {verdict}")
            if verdict != "CHANGES_REQUESTED" or round_no == 3:
                break

            print("Sending the review back to the developer...")
            await run_role(
                "developer",
                DEVELOPER_RULES,
                f"SPEC:\n{spec}\n\nPLAN:\n{plan}\n\n"
                f"A senior reviewer asked for changes. Fix ONLY the BLOCKER items:\n{review}",
                folder="repo",
                tools=DEV_TOOLS,
                budget=1.50,
            )
            built, _ = build_gate("repo")
            passed, _ = test_gate("repo")
            print(f"After rework: build {built}, all tests pass {passed}")
            if not (built and passed):
                print("Rework broke the build or tests. Stopping for a human decision.")
                break

        print(f"Final review verdict: {verdict}")
        ask_approval("the reviewed code (read reports/review-round-*.md and check git diff first)")
        mark_done("reviewer")
        
asyncio.run(main())
