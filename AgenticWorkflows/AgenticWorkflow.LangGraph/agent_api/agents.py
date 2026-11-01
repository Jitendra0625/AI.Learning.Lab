"""
The five agents, behind ONE function signature.

Milestone 1 (now): fake agents. They return canned text and fake token counts, so the whole
pipeline (gates, reject loops, limits, handoffs, persistence) can be built and tested for free.

Milestone 2: replace `fake_runner` with a runner that calls the Claude Agent SDK. Nothing else
changes. graph.py only knows "AgentRequest in, AgentResult out".

What an agent is allowed to see is decided HERE, in AgentRequest, not in the graph state:
    requirement  the original text (every stage gets it)
    handoff      the short summary of the PREVIOUS stage's approved result, and nothing else
    feedback     the reviewer's comment, only when this stage was rejected and is running again
Full outputs of earlier stages are not passed. Real agents will read files in their workspace.
"""
import asyncio
from dataclasses import dataclass
from typing import Awaitable, Callable


@dataclass(frozen=True)
class Stage:
    key: str        # used in node names and state keys
    title: str      # shown to the reviewer
    produces: str   # what this stage should hand over


STAGES: tuple[Stage, ...] = (
    Stage("intake_check", "Intake check", "spec.md: testable acceptance criteria from the given requirement and schema, gaps, open questions"),
    Stage("design_plan", "Design and plan", "design.md: structure and contracts, plus an ordered task list with a done-check per task"),
    Stage("implementer", "Implementer", "code changes and a passing build"),
    Stage("test_engineer", "Test engineer", "tests for each acceptance criterion, passing"),
    Stage("reviewer", "Reviewer", "review.md: verdict, risks, security notes"),
)


@dataclass(frozen=True)
class Artifact:
    """A file an agent produced, such as spec.md. The reviewer reads these on the review page."""
    path: str           # relative path inside the run's workspace
    content: str


@dataclass(frozen=True)
class AgentRequest:
    stage: str          # Stage.key
    title: str
    requirement: str
    handoff: str        # summary of the previous approved stage ("" for the first stage)
    feedback: str       # reviewer's comment when re-running after a rejection ("" otherwise)
    attempt: int        # 1 for the first try
    handoff_files: tuple[str, ...] = ()   # PATHS of the previous approved stage's files (not their content)
    run_id: str = ""      # which run this is; a real runner uses it to pick the run's workspace folder
    data_schema: str = ""      # the data schema the user supplied ("" if none). Untrusted text.
    profile: str = ""     # key of the solution profile, for example "dotnet-net9-console" (see profiles.py)
    constraints: str = "" # extra rules from the user ("" if none). Untrusted text.


@dataclass(frozen=True)
class AgentResult:
    output: str         # a short readable account of what the agent did, shown to the reviewer
    summary: str        # short text handed to the next stage (graph.py caps its length)
    tokens: int         # tokens this call used (real runner: from the SDK's usage / cost data)
    artifacts: tuple[Artifact, ...] = ()  # the files it produced (real runner: read back from the workspace)


AgentRunner = Callable[[AgentRequest], Awaitable[AgentResult]]

# Fake usage per stage, so the token budget can be exercised.
FAKE_TOKENS = {
    "intake_check": 900,
    "design_plan": 1_300,
    "implementer": 4_200,
    "test_engineer": 2_600,
    "reviewer": 1_100,
}


# The one file each fake stage "writes". A real stage writes whatever its job needs.
FAKE_FILES = {
    "intake_check": "spec.md",
    "design_plan": "design.md",
    "implementer": "changes.diff",
    "test_engineer": "tests.md",
    "reviewer": "review.md",
}


async def fake_runner(req: AgentRequest) -> AgentResult:
    await asyncio.sleep(0)   # behaves like a real awaited call: yields to the event loop
    lines = [f"[fake {req.title}, attempt {req.attempt}]", f"Requirement: {req.requirement}"]
    if req.handoff:
        lines.append(f"Built on the previous stage: {req.handoff}")
    if req.handoff_files:
        lines.append(f"Files from the previous stage: {', '.join(req.handoff_files)}")
    if req.feedback:
        lines.append(f"Revised to address reviewer feedback: {req.feedback}")
    text = "\n".join(lines)
    # The summary deliberately does NOT repeat the handoff, so summaries stay short and one
    # stage's text never leaks two stages down.
    summary = f"{req.title} finished (attempt {req.attempt})."
    if req.feedback:
        summary += f" Addressed: {req.feedback}"
    path = FAKE_FILES[req.stage]
    return AgentResult(
        output=text,
        summary=summary,
        tokens=FAKE_TOKENS[req.stage],
        artifacts=(Artifact(path=path, content=f"# {path} (attempt {req.attempt})\n\nContents of {path}\n\n{text}\n"),),
    )
