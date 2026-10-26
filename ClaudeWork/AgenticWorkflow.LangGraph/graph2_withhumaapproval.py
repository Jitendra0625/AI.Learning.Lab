from typing import TypedDict


from langgraph.graph import StateGraph, START, END
from langgraph.checkpoint.memory import MemorySaver
from langgraph.types import interrupt, Command

class AgentState(TypedDict):
    question:str
    passages: list[str]
    answer:str
    verdict:str
    retries:int
    approve:str
    
def retrieve(state:AgentState):
    attempt= state["retries"]+1
    print(f"retrieve: attempt {attempt}")
    return {"passages": [f"fake text, attempt {attempt}"], "retries": attempt}

def draft(state:AgentState):
    print("draft sees:", state)
    return {"answer": f"Answer to '{state['question']}' using {len(state['passages'])} passage(s)"}

def judge(state:AgentState):
    # fake judge: fails the first attempt, passes from the second
    verdict = "pass" if state["retries"] >= 5 else "fail"
    print(f"judge: {verdict}")
    return {"verdict": verdict}

def after_judge(state: AgentState):
    if state["verdict"] == "pass" or state["retries"] >= 3:
        return "done"
    return "retry"

def approve(state: AgentState):
    # the graph pauses here until a person answers
    decision = interrupt({"answer": state["answer"], "ask": "Approve this answer? yes/no"})
    return {"approved": decision}

builder= StateGraph(AgentState)
builder.add_node("retrieve", retrieve)
builder.add_node("draft", draft)
builder.add_node("judge", judge)
builder.add_node("approve", approve)
builder.add_edge(START,"retrieve")
builder.add_edge("retrieve", "draft")
builder.add_edge("draft", "judge")
builder.add_conditional_edges("judge", after_judge, {"retry": "retrieve", "done": "approve"}) 
builder.add_edge("approve", END) # after approval, go to end

graph = builder.compile(checkpointer=MemorySaver())

config = {"configurable": {"thread_id": "run-1"}}

result = graph.invoke(
    {"question": "What does MCOB say about affordability?", "retries": 0},
    config,
)
print("PAUSED, waiting for a person:")
print(result["__interrupt__"])

reply = input("Approve? (yes/no): ")
final = graph.invoke(Command(resume=reply), config)
print("FINAL:", final)