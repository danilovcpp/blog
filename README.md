# blog

## Spec Kit

В проект подключён [GitHub Spec Kit](https://github.com/github/spec-kit) 1.2.3
с интеграцией Codex и скриптами PowerShell.

- `.agents/skills/speckit-*/` — навыки Codex для работы со спецификациями.
- `.specify/` — шаблоны, скрипты и конституция проекта.
- `specs/` — спецификации функций, создаваемые во время работы.

В чате Codex последовательно вызовите:

1. `$speckit-constitution` — определить принципы проекта.
2. `$speckit-specify <описание функции>` — подготовить спецификацию.
3. `$speckit-plan` — составить технический план.
4. `$speckit-tasks` — разбить план на задачи.
5. `$speckit-implement` — выполнить задачи.

Навыки будут доступны со следующего сообщения. Если они не появились в списке,
перезапустите Codex.

### Локальный CLI

Версия CLI закреплена в `requirements-speckit.txt`. Окружение устанавливается
в `.tools/spec-kit/` и исключено из Git. Для установки на другой машине нужны
Python 3.11+ и Codex:

```powershell
python -m venv .tools/spec-kit
.\.tools\spec-kit\Scripts\python.exe -m pip install -r requirements-speckit.txt
.\.tools\spec-kit\Scripts\specify.exe version
```

Файлы интеграции уже находятся в репозитории. Для их повторной инициализации:

```powershell
.\.tools\spec-kit\Scripts\specify.exe init --here --force --non-interactive --integration codex --script ps
```

Документация: [интеграция Codex](https://github.github.io/spec-kit/integrations/codex.html).
