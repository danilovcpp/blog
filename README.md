# blog

## Blog Engine: Setup

Реализован каркас Phase 1 (T001–T008) из
[`tasks.md`](specs/001-blog-engine-mvp/tasks.md). Продуктовые endpoints, проверка
production-конфигурации, модель БД и команды Operator относятся к следующим фазам.
API сейчас не предоставляет маршрутов; Operator и performance harness завершаются
с кодом 2 и сообщением о недоступных в Setup командах.

### Требования и сборка

Нужны .NET SDK **10.0.402**, PowerShell 7, NuGet и Linux Docker Engine.
`global.json` запрещает подмену SDK и выбирает VSTest. Версии пакетов закреплены
централизованно, `packages.lock.json` всех семи проектов хранятся в Git.
Для xUnit v3 4.0.2 выбран официальный пакет `xunit.v3.mtp-off`: метапакет
`xunit.v3` этой версии включает MTP v2, несовместимый с выбранным VSTest на SDK 10.
См. [выбор MTP в xUnit](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform).

```powershell
dotnet tool restore
dotnet restore BlogEngine.slnx --locked-mode
dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror
dotnet format BlogEngine.slnx --verify-no-changes --no-restore
dotnet test tests/BlogEngine.UnitTests/BlogEngine.UnitTests.csproj -c Release --no-build
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build
```

CI повторяет эти проверки на Linux. Недоступность Docker/БД, пустой suite или
пропущенные тесты означают ошибку. Результаты тестов сохраняются в игнорируемом
`artifacts/`. На машине с ограниченными правами Docker-тестам нужен разрешённый
доступ к engine. При повторном использовании MSBuild server из другой среды
можно добавить `--disable-build-servers -m:1` к команде сборки.

### Границы проектов

- `Core` — независимые Domain/Application/Contracts модулей Accounts, Publishing,
  Discussions, Engagement и ReadModels; каталоги каркаса перечислены в `.csproj`.
- `Infrastructure` → `Core`: EF Core/Npgsql, Identity и другие внешние механизмы.
- `Api` и `Operator` → `Core` + `Infrastructure`: отдельные HTTP/console hosts.
- `UnitTests` работают без сети/БД; тестируют синхронизацию и управляемые часы
  фикстур через linked sources, без ссылки на integration assembly.
- `IntegrationTests` используют Testcontainers и настоящий ASP.NET Core/JwtBearer.
  `Performance` — отдельный console harness для будущего SC-005.

### Локальный PostgreSQL

`compose.yaml` содержит только PostgreSQL **18.6**, закреплённый digest, полученным
через `docker pull postgres:18.6`. Тот же образ используется тестами. БД создаётся
с UTF-8, locale/collation `C`, UTC, без дополнительных расширений. Порт доступен
только на loopback; данные находятся в локальном Docker volume.

Перед `docker compose up -d postgres` предоставьте защищённые переменные окружения:
`BLOG_POSTGRES_DB`, `BLOG_POSTGRES_USER`, `BLOG_POSTGRES_PASSWORD`;
`BLOG_POSTGRES_PORT` необязателен (5432). Пароль не передавайте в аргументах команд
или version-controlled файлах. Compose завершится ошибкой, если credentials не заданы.
Изменение env не перенастраивает уже инициализированный volume.

Bootstrap credentials Compose предназначены для локальной инициализации.
**API, Operator и миграции должны использовать разные учётные записи**:
API — необходимый DML и SELECT ролей, Operator — ограниченная запись ролей/аудита,
миграции — отдельный deployment-шаг с DDL. Runtime hosts не применяют миграции.
Создание principals/grants входит в следующие задачи согласно
[`operations.md`](specs/001-blog-engine-mvp/contracts/operations.md).

### Конфигурация без секретов

`appsettings.json` содержит пустые значения и имена параметров; рабочих адресов
Identity или паролей по умолчанию нет. Используются стандартные providers .NET:
защищённые переменные окружения/secret store среды, локально — user-secrets в
Development, с разными `UserSecretsId` у hosts.

| Host | Параметры |
| --- | --- |
| API | `ConnectionStrings:Blog`, `Identity:Issuer`, `Identity:Audience`, `Identity:JwksUrl` |
| API, при локальном HTTP JWKS | `Identity:AllowLocalHttp` (по умолчанию `false`) |
| API, CORS | `Api:AllowedOrigins` (явный allowlist) |
| API, будущий учёт просмотров | `AnonymousVisitors:ActiveKeyId`, `AnonymousVisitors:Keys:<keyId>` |
| API, публичный host | `AllowedHosts` (по умолчанию только localhost/127.0.0.1) |
| Operator | `ConnectionStrings:Blog`, `Identity:Issuer` |
| Миграционный процесс | Отдельный DDL connection string из защищённой конфигурации |

Для env используйте `__` вместо `:`, например `Identity__Issuer`.
Реальные issuer/audience/JWKS URL предоставляет оператор установки; production JWKS
использует HTTPS. Visitor HMAC key ring не является RSA-ключом Identity.
Валидация обязательной конфигурации появится в T018, visitor key ring — в US7.
Не указывайте секреты в исходниках, Git, логах, истории shell или отчётах.

### Изолированные фикстуры

`PostgresFixture` создаёт одноразовый контейнер, а для каждого сценария — отдельную
БД, применяет переданные EF migrations и удаляет БД после проверки. Каждый DbContext
создаётся отдельно; общие изменяемые данные между тестами отсутствуют. Setup
проверяет механизм собственной test-only миграцией `FixtureProbe`; production
`BlogDbContext` и первая миграция Accounts предусмотрены в T016. `EnsureCreated`
не используется.

`ApiFactory` запускает реальный API host; `JwksFixture` предоставляет loopback HTTP
JWKS server, одноразовые RSA/JWT, ротацию и управляемые ответы без живого Identity.
Setup smoke test подключает настоящий JwtBearer только в тестовом host; production
аутентификация относится к T019–T021. `AdjustableTimeProvider` управляет наблюдаемым
UTC без ожиданий, `ConcurrencyBarrier` синхронизирует операции с ограниченным
таймаутом и отменой. Подход основан на документации
[Testcontainers PostgreSQL](https://dotnet.testcontainers.org/modules/postgres/) и
[ASP.NET Core integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0).

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
