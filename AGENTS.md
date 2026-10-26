# Project tooling

GitHub Spec Kit 1.2.3 is configured for Codex in `.agents/skills/`.
Use the project-local CLI from the repository root:

```powershell
.\.tools\spec-kit\Scripts\specify.exe <command>
```

Installation instructions are in `README.md`; the CLI version is pinned in
`requirements-speckit.txt`. Spec Kit automation uses PowerShell scripts in
`.specify/scripts/powershell/`.

# Tech UI agent memory

For every task involving `mockups/tech`, first read:

- `mockups/tech/AGENTS.md`
- `mockups/tech/MEMORY.md`
- The latest entries in `mockups/tech/memory/task-log.md`

Every agent working on this prototype must update its memory and append a task
log entry after every task, including reviews, research, documentation changes,
and partially completed work. Follow the local `AGENTS.md` for the update format.
