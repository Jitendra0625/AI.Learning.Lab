"""
LangGraph workflow: your graph2_withhumaapproval.py, made importable by FastAPI.

Changes from your original:
  1. build_graph(checkpointer) instead of compiling at import time, so the web app owns the
     checkpointer (MemorySaver now, Postgres later).
  2. The demo run moved under `if __name__ == "__main__":`.
  3. State key renamed `approve` -> `approved` (the node returned a key the schema did not have,
     so the person's answer was silently dropped). Review reply is a dict {decision, comment}.
  4. Token budget in the state: every model-calling node adds its usage to `tokens_used`, and
     after_judge stops the loop when the budget is spent. The run still goes to the human with
     `stop_reason` set, so a person decides what to do. (The fake nodes report fake usage.)
  5. The review payload carries verdict, retries, stop_reason and token usage.
  6. RetryPolicy on the model nodes, for transient errors only (timeouts, dropped connections).
     When you swap in real model calls, add the provider's rate-limit exception to retry_on.
  7. The fake judge's pass threshold is now an explicit constant that a run can override with
     `judge_pass_at`, so the "pass" path can be tested. With the default (5) the judge never
     passes inside the 3-retry cap, so every run reaches the human with verdict "fail".

Run the console demo:  python graph.py
"""
from typing import TypedDict

from langgraph.checkpoint.memory import MemorySaver
from langgraph.graph import END, START, StateGraph
from langgraph.types import Command, RetryPolicy, interrupt

MAX_RETRIES = 3
DEFAULT_TOKEN_BUDGET = 150_000
DEFAULT_JUDGE_PASS_AT = 5      # judge passes once retries >= this; > MAX_RETRIES means "never pass"

# Fake usage per node call. Real nodes: read it from the model response (usage field).
FAKE_USAGE = {"draft": 1_200, "judge": 400}

# Transient errors only. Do NOT retry bugs. Add your provider's rate-limit exception later.
TRANSIENT = RetryPolicy(max_attempts=3, retry_on=(TimeoutError, ConnectionError))


class AgentState(TypedDict, total=False):
    question: str
    passages: list[str]
    answer: str
    verdict: str
    retries: int
    tokens_used: int      # running total across all model calls in this run
    token_budget: int     # stop retrying once tokens_used reaches this
    judge_pass_at: int    # test hook for the fake judge
    approved: str         # "approve" or "reject", set by the human review
    review_comment: str   # optional note from the reviewer


def stop_reason(state: AgentState) -> str:
    """Why the loop is handing over to the human. One definition, used by routing and payload."""
    if state.get("verdict") == "pass":
        return "pass"
    if state.get("tokens_used", 0) >= state.get("token_budget", DEFAULT_TOKEN_BUDGET):
        return "budget"
    if state.get("retries", 0) >= MAX_RETRIES:
        return "max_retries"
    return "continue"


def retrieve(state: AgentState):
    attempt = state["retries"] + 1
    print(f"retrieve: attempt {attempt}")
    return {"passages": [f"fake text, attempt {attempt}"], "retries": attempt}


def draft(state: AgentState):
    print(f"draft: attempt {state['retries']}, tokens so far {state.get('tokens_used', 0)}")
    return {
        "answer": f"Answer to '{state['question']}' using {len(state['passages'])} passage(s)",
        "tokens_used": state.get("tokens_used", 0) + FAKE_USAGE["draft"],
    }


def judge(state: AgentState):
    pass_at = state.get("judge_pass_at", DEFAULT_JUDGE_PASS_AT)
    verdict = "pass" if state["retries"] >= pass_at else "fail"
    print(f"judge: {verdict}")
    return {
        "verdict": verdict,
        "tokens_used": state.get("tokens_used", 0) + FAKE_USAGE["judge"],
    }


def after_judge(state: AgentState):
    return "retry" if stop_reason(state) == "continue" else "done"


def approve(state: AgentState):
    # The graph pauses here until a person answers. Keep side effects OUT of this node
    # before the interrupt: on resume the node runs again from its first line.
    review = interrupt(
        {
            "answer": state["answer"],
            "verdict": state["verdict"],
            "retries": state["retries"],
            "stop_reason": stop_reason(state),
            "tokens_used": state.get("tokens_used", 0),
            "token_budget": state.get("token_budget", DEFAULT_TOKEN_BUDGET),
            "ask": "Approve this answer?",
        }
    )
    decision = review["decision"]
    if decision not in ("approve", "reject"):
        raise ValueError(f"decision must be approve or reject, got {decision!r}")
    return {"approved": decision, "review_comment": review.get("comment", "")}


def build_graph(checkpointer):
    builder = StateGraph(AgentState)
    builder.add_node("retrieve", retrieve)
    builder.add_node("draft", draft, retry_policy=TRANSIENT)
    builder.add_node("judge", judge, retry_policy=TRANSIENT)
    builder.add_node("approve", approve)
    builder.add_edge(START, "retrieve")
    builder.add_edge("retrieve", "draft")
    builder.add_edge("draft", "judge")
    builder.add_conditional_edges("judge", after_judge, {"retry": "retrieve", "done": "approve"})
    builder.add_edge("approve", END)  # after approval, go to end
    return builder.compile(checkpointer=checkpointer)


if __name__ == "__main__":
    graph = build_graph(MemorySaver())
    config = {"configurable": {"thread_id": "run-1"}, "recursion_limit": 30}

    result = graph.invoke(
        {"question": "What does MCOB say about affordability?", "retries": 0, "tokens_used": 0,
         "token_budget": DEFAULT_TOKEN_BUDGET},
        config,
    )
    print("PAUSED, waiting for a person:")
    print(result["__interrupt__"])

    reply = input("Approve? (yes/no): ")
    decision = "approve" if reply.strip().lower() in ("y", "yes") else "reject"
    final = graph.invoke(Command(resume={"decision": decision, "comment": ""}), config)
    print("FINAL:", final)
