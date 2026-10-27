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
    Stage("spec_analyst", "Spec analyst", "spec.md: acceptance criteria, edge cases, open questions"),
    Stage("designer", "Designer", "design.md: contracts, data model, files to change, test plan"),
    Stage("implementer", "Implementer", "code changes and a passing build"),
    Stage("test_engineer", "Test engineer", "tests for each acceptance criterion, passing"),
    Stage("reviewer", "Reviewer", "review.md: verdict, risks, security notes"),
)


@dataclass(frozen=True)
class AgentRequest:
    stage: str          # Stage.key
    title: str
    requirement: str
    handoff: str        # summary of the previous approved stage ("" for the first stage)
    feedback: str       # reviewer's comment when re-running after a rejection ("" otherwise)
    attempt: int        # 1 for the first try


@dataclass(frozen=True)
class AgentResult:
    output: str         # the full result, shown to the reviewer
    summary: str        # short text handed to the next stage (graph.py caps its length)
    tokens: int         # tokens this call used (real runner: from the SDK's usage / cost data)


AgentRunner = Callable[[AgentRequest], Awaitable[AgentResult]]

# Fake usage per stage, so the token budget can be exercised.
FAKE_TOKENS = {
    "spec_analyst": 900,
    "designer": 1_300,
    "implementer": 4_200,
    "test_engineer": 2_600,
    "reviewer": 1_100,
}


async def fake_runner(req: AgentRequest) -> AgentResult:
    await asyncio.sleep(0)   # behaves like a real awaited call: yields to the event loop
    lines = [f"[fake {req.title}, attempt {req.attempt}]", f"Requirement: {req.requirement}"]
    if req.handoff:
        lines.append(f"Built on the previous stage: {req.handoff}")
    if req.feedback:
        lines.append(f"Revised to address reviewer feedback: {req.feedback}")
    # The summary deliberately does NOT repeat the handoff, so summaries stay short and one
    # stage's text never leaks two stages down.
    summary = f"{req.title} finished (attempt {req.attempt})."
    if req.feedback:
        summary += f" Addressed: {req.feedback}"
    return AgentResult(output="\n".join(lines), summary=summary, tokens=FAKE_TOKENS[req.stage])
