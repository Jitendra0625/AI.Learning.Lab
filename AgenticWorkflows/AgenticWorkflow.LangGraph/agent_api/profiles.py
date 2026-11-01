"""
Solution profiles: everything that depends on WHAT is being built, kept out of the engine.

The engine (graph, gates, limits, audit, runner) is the same for every solution. A profile holds
the facts that change with the solution type and platform: the project template, the build and
test commands, which commands agents may run, and the conventions to follow.

Adding another stack later (Python, Node) means adding profiles here and testing them on a sample
project. The graph, the API and the database do not change.

Today only the .NET family exists. Nothing here runs a command; a profile is plain data.
"""
from dataclasses import dataclass

# solution type -> (label, `dotnet new` template, extra conventions)
SOLUTION_TYPES = {
    "console": (
        "Console application",
        "console",
        ("Keep Program.cs thin: parse input, call a service class, print the result.",),
    ),
    "api": (
        "Web API (ASP.NET Core)",
        "webapi",
        (
            "Keep endpoints thin; put logic in service classes.",
            "Return validation problems as ProblemDetails with a 400 status.",
        ),
    ),
    "webapp": (
        "Web application (ASP.NET Core Razor Pages)",
        "webapp",
        ("Keep page models thin; put logic in service classes.",),
    ),
}

# platform -> (target framework, label)
PLATFORMS = {
    "net9": ("net9.0", ".NET 9"),
    "net10": ("net10.0", ".NET 10"),
}

COMMON_CONVENTIONS = (
    "Nullable reference types enabled.",
    "One public class per file; file name matches the class name.",
    "Async methods end in Async.",
    "Tests use xUnit in a separate tests/ project.",
)


@dataclass(frozen=True)
class Profile:
    key: str
    label: str
    solution_type: str
    platform: str
    template_command: str
    build_command: str
    test_command: str
    allowed_commands: tuple[str, ...]
    conventions: tuple[str, ...]
    definition_of_done: str


def profile_key(solution_type: str, platform: str) -> str:
    return f"dotnet-{platform}-{solution_type}"


def _build_all() -> dict[str, Profile]:
    profiles = {}
    for stype, (stype_label, template, extra) in SOLUTION_TYPES.items():
        for platform, (framework, platform_label) in PLATFORMS.items():
            key = profile_key(stype, platform)
            profiles[key] = Profile(
                key=key,
                label=f"{stype_label} on {platform_label}",
                solution_type=stype,
                platform=platform,
                template_command=f"dotnet new {template} -f {framework}",
                build_command="dotnet build",
                test_command="dotnet test",
                allowed_commands=("dotnet", "git diff"),
                conventions=COMMON_CONVENTIONS + extra,
                definition_of_done="The build passes, all tests pass, and there are no new compiler warnings.",
            )
    return profiles


PROFILES: dict[str, Profile] = _build_all()


def get_profile(key: str) -> Profile:
    try:
        return PROFILES[key]
    except KeyError:
        raise ValueError(f"unknown profile {key!r}; known: {', '.join(PROFILES)}") from None


def describe(profile: Profile) -> str:
    """The profile as a short block of text for an agent's prompt. This is OUR configuration,
    not user text, so it is not fenced in data tags."""
    rules = "\n".join(f"- {c}" for c in profile.conventions)
    return (
        f"Target solution: {profile.label}.\n"
        f"Create projects with: {profile.template_command}\n"
        f"Build: {profile.build_command}. Test: {profile.test_command}.\n"
        f"Conventions:\n{rules}\n"
        f"Definition of done: {profile.definition_of_done}"
    )
