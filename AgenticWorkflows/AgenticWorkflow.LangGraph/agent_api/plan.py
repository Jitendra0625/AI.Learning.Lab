"""
The task list that the Design and plan agent writes (tasks.json), and the checks on it.

Why check it in code: the Implementer will later work through this list one task at a time and
run each task's done-check. A list that is vague, huge, or holds a strange command would be
dangerous or useless, so the CODE checks it, not the agent that wrote it.

The file looks like this:

    {"tasks": [
        {"id": 1, "title": "Create the order model", "files": ["src/Order.cs"], "done_check": "dotnet build"},
        {"id": 2, "title": "Add the order service", "files": ["src/OrderService.cs", "tests/OrderServiceTests.cs"],
         "done_check": "dotnet test"}
    ]}

Nothing here runs a command. done_check is only text; it must start with the profile's build or
test command and must not chain other commands.
"""
import json

from profiles import get_profile

MAX_TASKS = 15
MAX_FILES_PER_TASK = 10
MAX_TITLE_CHARS = 120
MAX_CHECK_CHARS = 200
FORBIDDEN_IN_CHECK = set(";&|`$<>\n\\")      # no chaining, redirects or substitutions


class PlanError(ValueError):
    """tasks.json is not acceptable. The message says why, in plain words."""


def _safe_path(path: str) -> bool:
    parts = path.replace("\\", "/").split("/")
    return bool(path) and len(path) <= 200 and path[0] not in "/\\" and ":" not in path and ".." not in parts and "" not in parts


def validate_tasks(text: str, profile_key: str = "") -> list[dict]:
    """Return the clean task list, or raise PlanError."""
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise PlanError(f"tasks.json is not valid JSON ({exc.msg} at line {exc.lineno})") from None
    tasks = data.get("tasks") if isinstance(data, dict) else None
    if not isinstance(tasks, list) or not tasks:
        raise PlanError('tasks.json must be an object with a non-empty "tasks" list')
    if len(tasks) > MAX_TASKS:
        raise PlanError(f"{len(tasks)} tasks; the limit is {MAX_TASKS}. Make the tasks bigger or the scope smaller")

    allowed_starts = ()
    if profile_key:
        profile = get_profile(profile_key)
        allowed_starts = (profile.build_command, profile.test_command)

    clean = []
    for position, task in enumerate(tasks, start=1):
        if not isinstance(task, dict):
            raise PlanError(f"task {position} is not an object")
        if task.get("id") != position:
            raise PlanError(f"task ids must be 1, 2, 3 ... in order; task {position} has id {task.get('id')!r}")
        title = task.get("title")
        if not isinstance(title, str) or not title.strip() or len(title) > MAX_TITLE_CHARS:
            raise PlanError(f"task {position} needs a title of 1 to {MAX_TITLE_CHARS} characters")
        files = task.get("files")
        if not isinstance(files, list) or not files or len(files) > MAX_FILES_PER_TASK:
            raise PlanError(f"task {position} needs 1 to {MAX_FILES_PER_TASK} files")
        for f in files:
            if not isinstance(f, str) or not _safe_path(f):
                raise PlanError(f"task {position} has an unsafe file path: {f!r}")
        check = task.get("done_check")
        if not isinstance(check, str) or not check.strip() or len(check) > MAX_CHECK_CHARS:
            raise PlanError(f"task {position} needs a done_check")
        if FORBIDDEN_IN_CHECK & set(check):
            raise PlanError(f"task {position}: done_check must be one plain command, no chaining or redirects")
        if allowed_starts and not check.startswith(allowed_starts):
            raise PlanError(f"task {position}: done_check must start with one of: {', '.join(allowed_starts)}")
        clean.append({"id": position, "title": title.strip(), "files": list(files), "done_check": check.strip()})
    return clean


def render_tasks(tasks: list[dict]) -> str:
    """The task list as plain text for the review page and the email."""
    lines = []
    for t in tasks:
        lines.append(f"{t['id']}. {t['title']}")
        lines.append(f"     files: {', '.join(t['files'])}")
        lines.append(f"     done when: {t['done_check']}")
    return "\n".join(lines)
