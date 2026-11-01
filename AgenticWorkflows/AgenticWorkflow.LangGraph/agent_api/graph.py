"""
The pipeline: five agents in a row, a human review gate after each one.

    intake_check -> intake_check_review -> design_plan -> design_plan_review -> ... -> reviewer_review -> finish
                         |  reject (with a comment): back to the SAME agent, up to MAX_ATTEMPTS_PER_STAGE
                         |  approve: on to the next agent

How it stops (the `finish` node records which one happened in `outcome` and `stop_reason`):
    APPROVED         all five stages approved
    REJECTED         one stage was rejected MAX_ATTEMPTS_PER_STAGE times (a person must rethink it)
    STOPPED_BUDGET   tokens_used reached token_budget. Checked after each review, so a single agent
                     call can overshoot by its own usage; per-agent caps (milestone 2) bound that.

Rules this file follows:
  * The agents are behind `runner` (agents.py). The graph never knows if they are fake or real.
  * A review gate does nothing except ask and record. interrupt() makes the node run again from
    its first line on resume, so no side effect (mail, writes) may sit before it.
  * Handoff between stages is the previous stage's short SUMMARY (capped), never full outputs.
  * Transient errors (timeouts, dropped connections) are retried per node; bugs are not.

Console demo:   python graph.py            (answer the review prompts by typing)
Diagram:        python graph.py --mermaid  (paste the output into any Mermaid viewer / README)
"""
import hashlib
import operator
import sys
from typing import Annotated, TypedDict

from langgraph.checkpoint.memory import MemorySaver
from langgraph.graph import END, START, StateGraph
from langgraph.types import Command, RetryPolicy, interrupt

from agents import STAGES, AgentRequest, AgentRunner, fake_runner

MAX_ATTEMPTS_PER_STAGE = 3
DEFAULT_TOKEN_BUDGET = 150_000
MAX_SUMMARY_CHARS = 600        # a handoff is a short summary, enforced here in code
MAX_ARTIFACTS_PER_ATTEMPT = 20
MAX_ARTIFACT_CHARS = 100_000   # per file. The state is saved at every step, so files must stay modest.

# Transient errors only. Do NOT retry bugs. Add the provider's rate-limit exception later.
TRANSIENT = RetryPolicy(max_attempts=3, retry_on=(TimeoutError, ConnectionError))


def prepare_artifacts(artifacts) -> list[dict]:
    """Check the files an agent returned and add a SHA-256 to each.

    A bad path or an oversized file is an ERROR (the run fails with a clear message), never a
    silent cut: a reviewer must be able to trust that the file shown is the file produced.
    ValueError is not retried by the node's retry policy.
    """
    if len(artifacts) > MAX_ARTIFACTS_PER_ATTEMPT:
        raise ValueError(f"an agent returned {len(artifacts)} files; the limit is {MAX_ARTIFACTS_PER_ATTEMPT}")
    seen, prepared = set(), []
    for item in artifacts:
        path = item.path
        parts = path.replace("\\", "/").split("/")
        if not path or len(path) > 200 or path[0] in "/\\" or ":" in path or ".." in parts or "" in parts:
            raise ValueError(f"unsafe artifact path: {path!r}")
        if path in seen:
            raise ValueError(f"the same file was returned twice: {path!r}")
        seen.add(path)
        if len(item.content) > MAX_ARTIFACT_CHARS:
            raise ValueError(f"{path} is {len(item.content)} characters; the limit is {MAX_ARTIFACT_CHARS}")
        raw = item.content.encode("utf-8")
        prepared.append(
            {"path": path, "content": item.content, "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw)}
        )
    return prepared


def merge_dicts(old: dict | None, new: dict) -> dict:
    """Reducer: a node returns {stage: value} and it is MERGED into the saved dict,
    instead of replacing the whole dict."""
    return {**(old or {}), **new}


class PipelineState(TypedDict, total=False):
    run_id: str                               # lets a real runner use one workspace folder per run
    requirement: str
    data_schema: str                               # optional data schema supplied by the user
    profile: str                              # solution profile key, for example "dotnet-net9-console"
    constraints: str                          # optional extra rules supplied by the user
    drafts: Annotated[dict, merge_dicts]      # stage -> latest attempt {output, summary, attempt, artifacts}
    approved: Annotated[dict, merge_dicts]    # stage -> accepted result {output, summary, files (paths only)}
    attempts: Annotated[dict, merge_dicts]    # stage -> how many times it has run
    feedback: str                             # reviewer comment for the stage about to re-run
    last_decision: str                        # "approve" or "reject", set by a review gate
    last_stage: str                           # the stage that decision was about
    history: Annotated[list, operator.add]    # every review decision, in order (audit trail)
    tokens_used: int
    token_budget: int
    outcome: str                              # set by `finish`
    stop_reason: str                          # set by `finish`
    stopped_at: str                           # stage key where it stopped early, else ""


# ---------------------------------------------------------------- decisions (plain functions, easy to test)

def over_budget(state: PipelineState) -> bool:
    return state.get("tokens_used", 0) >= state.get("token_budget", DEFAULT_TOKEN_BUDGET)


def decide(state: PipelineState, index: int) -> str:
    """After the review gate of stage `index`: "next", "retry" or "finish"."""
    stage = STAGES[index]
    if state["last_decision"] == "approve":
        if index == len(STAGES) - 1 or over_budget(state):
            return "finish"
        return "next"
    if state.get("attempts", {}).get(stage.key, 0) >= MAX_ATTEMPTS_PER_STAGE or over_budget(state):
        return "finish"
    return "retry"


def finish_reason(state: PipelineState) -> tuple[str, str]:
    """(outcome, stop_reason) for a run that reached `finish`."""
    if len(state.get("approved", {})) == len(STAGES):
        return "APPROVED", "all_stages_approved"
    stage = state.get("last_stage", "")
    if state.get("last_decision") == "reject" and state.get("attempts", {}).get(stage, 0) >= MAX_ATTEMPTS_PER_STAGE:
        return "REJECTED", "max_rejects"
    return "STOPPED_BUDGET", "budget"


# ---------------------------------------------------------------- nodes

def make_agent_node(index: int, runner: AgentRunner):
    stage = STAGES[index]
    previous = STAGES[index - 1] if index else None

    async def agent(state: PipelineState):
        attempt = state.get("attempts", {}).get(stage.key, 0) + 1
        before = state.get("approved", {}).get(previous.key, {}) if previous else {}
        handoff = before.get("summary", "")
        result = await runner(
            AgentRequest(
                stage=stage.key,
                title=stage.title,
                requirement=state["requirement"],
                handoff=handoff,
                handoff_files=tuple(before.get("files", ())),    # paths only, never the content
                feedback=state.get("feedback", ""),
                attempt=attempt,
                run_id=state.get("run_id", ""),
                data_schema=state.get("data_schema", ""),
                profile=state.get("profile", ""),
                constraints=state.get("constraints", ""),
            )
        )
        artifacts = prepare_artifacts(result.artifacts)
        return {
            "drafts": {
                stage.key: {
                    "output": result.output,
                    "summary": result.summary[:MAX_SUMMARY_CHARS],
                    "attempt": attempt,
                    "tokens": result.tokens,     # what THIS attempt cost (tokens_used is the running total)
                    "artifacts": artifacts,
                }
            },
            "attempts": {stage.key: attempt},
            "tokens_used": state.get("tokens_used", 0) + result.tokens,
        }

    agent.__name__ = stage.key
    return agent


def make_review_node(index: int):
    stage = STAGES[index]

    def review(state: PipelineState):
        draft = state["drafts"][stage.key]
        # The graph pauses here until a person answers. No side effects before this line.
        answer = interrupt(
            {
                # Identifies THIS review. A decision must name it, so a late or repeated click
                # can never land on a later review of the same run.
                "gate": f"{stage.key}:{draft['attempt']}",
                "stage": stage.key,
                "title": stage.title,
                "step": index + 1,
                "total_steps": len(STAGES),
                "attempt": draft["attempt"],
                "max_attempts": MAX_ATTEMPTS_PER_STAGE,
                "output": draft["output"],
                "summary": draft["summary"],
                "artifacts": draft["artifacts"],        # the files the reviewer reads, each with its SHA-256
                "previous_feedback": state.get("feedback", ""),   # what this attempt was asked to fix
                "attempt_tokens": draft["tokens"],
                "tokens_used": state.get("tokens_used", 0),
                "token_budget": state.get("token_budget", DEFAULT_TOKEN_BUDGET),
                "ask": f"Approve the {stage.title} result?",
            }
        )
        decision = answer["decision"]
        if decision not in ("approve", "reject"):
            raise ValueError(f"decision must be approve or reject, got {decision!r}")
        comment = answer.get("comment", "")
        update = {
            "last_decision": decision,
            "last_stage": stage.key,
            "history": [{"stage": stage.key, "attempt": draft["attempt"], "decision": decision, "comment": comment,
                         "attempt_tokens": draft["tokens"]}],
        }
        if decision == "approve":
            update["approved"] = {
                stage.key: {
                    "output": draft["output"],
                    "summary": draft["summary"],
                    "files": [a["path"] for a in draft["artifacts"]],   # the next stage gets paths, not content
                }
            }
            update["feedback"] = ""
        else:
            update["feedback"] = comment or "(rejected without a comment)"
        return update

    review.__name__ = f"{stage.key}_review"
    return review


def finish(state: PipelineState):
    outcome, reason = finish_reason(state)
    return {
        "outcome": outcome,
        "stop_reason": reason,
        "stopped_at": "" if outcome == "APPROVED" else state.get("last_stage", ""),
    }


def build_graph(checkpointer, runner: AgentRunner = fake_runner):
    builder = StateGraph(PipelineState)
    for i, stage in enumerate(STAGES):
        builder.add_node(stage.key, make_agent_node(i, runner), retry_policy=TRANSIENT)
        builder.add_node(f"{stage.key}_review", make_review_node(i))
        builder.add_edge(stage.key, f"{stage.key}_review")
        paths = {"retry": stage.key, "finish": "finish"}
        if i < len(STAGES) - 1:
            paths["next"] = STAGES[i + 1].key
        builder.add_conditional_edges(f"{stage.key}_review", lambda s, i=i: decide(s, i), paths)
    builder.add_node("finish", finish)
    builder.add_edge(START, STAGES[0].key)
    builder.add_edge("finish", END)
    return builder.compile(checkpointer=checkpointer)


# ---------------------------------------------------------------- console demo

def _demo():
    import asyncio

    async def main():
        graph = build_graph(MemorySaver())
        config = {"configurable": {"thread_id": "demo"}, "recursion_limit": 60}
        result = await graph.ainvoke(
            {"requirement": "Add POST /orders with an Idempotency-Key header", "tokens_used": 0,
             "token_budget": DEFAULT_TOKEN_BUDGET},
            config,
        )
        while "__interrupt__" in result:
            p = result["__interrupt__"][0].value
            print(f"\n=== Step {p['step']}/{p['total_steps']}: {p['title']} (attempt {p['attempt']}/{p['max_attempts']}) ===")
            print(p["output"])
            reply = input("approve / reject? (a/r): ").strip().lower()
            decision = "approve" if reply.startswith("a") else "reject"
            comment = input("comment (optional): ") if decision == "reject" else ""
            result = await graph.ainvoke(Command(resume={"decision": decision, "comment": comment}), config)
        print("\nFINISHED:", result["outcome"], "|", result["stop_reason"], "| tokens:", result["tokens_used"])

    asyncio.run(main())


if __name__ == "__main__":
    if "--mermaid" in sys.argv:
        print(build_graph(MemorySaver()).get_graph().draw_mermaid())
    else:
        _demo()
