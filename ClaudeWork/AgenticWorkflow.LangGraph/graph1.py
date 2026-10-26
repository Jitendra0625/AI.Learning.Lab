from typing import TypedDict
from langgraph.graph import StateGraph, START, END


class AgentState(TypedDict):
    question:str
    passages: list[str]
    answer:str
    verdict:str
    retries:int
    
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

builder= StateGraph(AgentState)
builder.add_node("retrieve", retrieve)
builder.add_node("draft", draft)
builder.add_node("judge", judge)
builder.add_edge(START,"retrieve")
builder.add_edge("retrieve", "draft")
builder.add_edge("draft", "judge")
builder.add_conditional_edges("judge", after_judge, {"retry": "retrieve", "done": END}) # if verdict is pass or retries >= 3, go to done, else retry 


graph= builder.compile()
result =graph.invoke({"question": "What does MCOB say about affordability?", "retries": 0})
print("FINAL:", result)