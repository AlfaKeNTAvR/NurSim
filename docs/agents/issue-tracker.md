# Issue tracker: Linear

Issues and specs for this repo live in Linear, in the project **NurSim** (team **WPI**, key `HIR`). Use the Linear MCP tools (`mcp__claude_ai_Linear__*`) for all operations. Every issue created for this repo belongs to that project.

Project: https://linear.app/hiro-lab-wpi/project/nursim-f2787e86564d

The WPI team is public inside the workspace, so lab members can see these issues. Never put participant-identifying data (names, faces, voice, interview quotes tied to a person) in an issue.

## Conventions

- **Create an issue**: `save_issue` with `team: "WPI"`, `project: "NurSim"`, `title`, and a Markdown `description`.
- **Read an issue**: `get_issue` with the identifier (e.g. `HIR-12`), then `list_comments` for the discussion.
- **List issues**: `list_issues` with `project: "NurSim"`, filtered by `label` and `state` as needed.
- **Comment on an issue**: `save_comment` with the issue identifier and `body`.
- **Apply / remove labels**: `save_issue` with the issue `id` and the full `labels` list (it replaces the set, so include labels to keep).
- **Close**: `save_comment` with the reason, then `save_issue` with `state: "Done"` (or `"Canceled"` for wontfix).

Labels that don't exist yet are created at team level with `save_issue_label`.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` to treat external GitHub PRs as feature requests; `/triage` reads this flag.)_

## When a skill says "publish to the issue tracker"

Create a Linear issue in the NurSim project.

## When a skill says "fetch the relevant ticket"

`get_issue` with the identifier, plus `list_comments`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: one issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body.
- **Child ticket**: an issue created with `parentId` set to the map, so it shows as a Linear sub-issue. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, it is assigned to the driving person.
- **Blocking**: Linear's native "blocked by" relation, set on the child via `save_issue`. Where that is not available, fall back to a `Blocked by: HIR-<n>, HIR-<n>` line at the top of the child body. A ticket is unblocked when every blocker is Done or Canceled.
- **Frontier query**: `list_issues` for the map's open children; drop any with an open blocker or an assignee; first in map order wins.
- **Claim**: `save_issue` with `assignee: "me"`, the session's first write.
- **Resolve**: `save_comment` with the answer, set the state to Done, then append a context pointer (gist + link) to the map's Decisions-so-far.
