# Project tooling

GitHub Spec Kit 1.2.3 is configured for Codex in `.agents/skills/`.
Use the project-local CLI from the repository root:

```powershell
.\.tools\spec-kit\Scripts\specify.exe <command>
```

Installation instructions are in `README.md`; the CLI version is pinned in
`requirements-speckit.txt`. Spec Kit automation uses PowerShell scripts in
`.specify/scripts/powershell/`.
