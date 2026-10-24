# blog

## Blog Engine: Publishing, Reading, Accounts, Discussions and Moderation MVP

Реализованы Phase 1–8 (T001–T099) из
[`tasks.md`](specs/001-blog-engine-mvp/tasks.md): общие правила Core, Accounts,
миграции Accounts/Publishing/Discussions, Bearer/JWKS, HTTP-контракты, проверка конфигурации и health.
API предоставляет `/health/live` и `/health/ready`; OpenAPI 3.1 и Swagger UI включены
только в Development. Author/Admin могут создавать Draft, редактировать, публиковать,
снимать с публикации и удалять материалы через `/api/v1/publications` и workspace
`/api/v1/workspace/publications`. Категории и теги доступны для публичного чтения,
изменять их может Admin. Опубликованный материал читается по ID или
`/api/v1/publications/by-slug/{slug}`. Markdown возвращается как недоверенный source;
движок не рендерит HTML и не загружает обложки. GET не учитывает просмотры.

Публичная лента `GET /api/v1/publications` принимает `page` (по умолчанию 1),
`pageSize` (по умолчанию 20, максимум 100), `sort=recent|popular` (по умолчанию recent)
и повторяемые `categoryIds`/`tagIds`: хотя бы одна из выбранных категорий и все выбранные
теги. Максимум 5 категорий и 20 тегов; неизвестные, повторные и неверные ID дают 400.
Ответ содержит карточки без Markdown и признак `hasNextPage`; допустимая дальняя страница
пуста. Recent сортирует по первой публикации/ID DESC, popular — по лайкам/первой
публикации/ID DESC. `GET /api/v1/publications/{id}/counters` возвращает текущие bigint
счётчики и личный лайк. Лента, detail и counters принимают optional Bearer; плохой JWT
даёт отказ. Анонимный `isLikedByCurrentUser` всегда false.

Reading-тесты используют неизменный набор из 250 материалов с совпадающими датами,
лайками двух пользователей, Draft и tombstones. SQL/EXPLAIN 96 вариантов чтения
сохраняются в `artifacts/validation/queries/us2-*.json`; лента выполняет не более
5 data-запросов плюс 3 фиксированных auth/bootstrap-запроса при Bearer. Нагрузочная
приёмка SC-005 остаётся в финальной фазе.

Для изменения существующего ресурса нужен `X-Resource-Version` из workspace/taxonomy DTO;
устаревшая версия даёт 409. Slug неизменен после первой публикации и остаётся занят
после удаления. Public detail не содержит edit version/state; workspace list не содержит
Markdown. Публичные content-ответы используют `private, no-store`/`Vary: Authorization`.
`GET /api/v1/me` создаёт локальную связь с Reader и возвращает только ID/текущие роли.
Повышенные роли выдаёт отдельный Operator: users find, roles list/grant/revoke с
атомарным неизменяемым аудитом, в том числе для no-op. Порядок первого Admin,
exit codes и проверка неизвестного commit описаны в [roles.md](docs/operations/roles.md).
Performance harness пока возвращает 2.

Обсуждение Published-материала доступно через
`GET /api/v1/publications/{id}/comments`: без `parentId` возвращаются корни,
с `parentId` — только прямые ответы доступного узла. Порядок `createdAt/id ASC`,
`page=1`, `pageSize=20` по умолчанию, максимум 100; ответ содержит `hasNextPage`.
Reader создаёт комментарий через POST того же маршрута с `{ content, parentId? }`.
Текст — простой недоверенный текст, после Unicode trim 1–5000 scalar values;
корень имеет глубину 1, максимум — 5. Новые комментарии и правки Visible видны сразу.

`GET /api/v1/comments/{commentId}` возвращает приватный контекст владельцу/Admin.
PUT с `{ content }` и DELETE требуют `X-Resource-Version` комментария. Владелец
Hidden получает `content=null` и версию для удаления; редактировать Hidden может
Admin с сохранением состояния. Удаление необратимо очищает текст, сохраняя ответы;
повтор с текущей версией успешен без смены версии/дат. Публичные Hidden/Deleted
с видимыми потомками возвращаются заглушками с `content=null`, `author=null`.
Узлы без видимых потомков пропускаются. Публичный DTO не содержит edit version.
Все операции требуют Published-материал, GET не учитывает просмотры.

Moderator/Admin получают `GET /api/v1/moderation/comments` с необязательными
`publicationId`, `state=Visible|Hidden|Deleted` и стандартными `page`/`pageSize`.
Список содержит только комментарии Published-материалов, включая сохранённый Hidden
текст/автора и edit version, в порядке `createdAt/id DESC`. Заданный закрытый,
удалённый или отсутствующий материал даёт 404. Очереди предварительного одобрения нет.

`POST /api/v1/comments/{commentId}/hide` и `/restore` требуют Bearer Moderator/Admin,
`X-Resource-Version` комментария и отсутствующего body. Переходы сохраняют текст/автора;
повтор текущего состояния с текущей версией успешен без изменения версии/дат/счётчиков,
устаревшая версия всегда даёт 409. Deleted нельзя скрыть или восстановить.
Изменение узла и максимум четырёх ancestor counts фиксируется атомарно; видимые ответы
остаются доступны через публичные заглушки. Владение материалом не даёт права модерации,
Moderator не может переписывать/удалять чужие комментарии. Все ответы модерации — `no-store`.
SQL/EXPLAIN 16 вариантов списка на 6000 комментариях сохраняются в
`artifacts/validation/queries/us5-moderation.json`, обоснование индексов — в
`artifacts/validation/us5-queries.md`.

Reader ставит и снимает собственный лайк через bodyless PUT/DELETE
`/api/v1/publications/{id}/like` с обязательным Bearer. Оба метода возвращают
200 с `{ likeCount, viewCount, isLikedByCurrentUser }` и `Cache-Control: no-store`.
Повтор желаемого состояния успешен без дополнительной дельты; `X-Resource-Version`
не требуется. Закрытый/удалённый/отсутствующий материал даёт 404 для любой роли.
Источник лайка и bigint-счётчик изменяются атомарно под блокировкой публикации;
лайки не меняют edit version, даты или контент. Popular order сразу отражает записи,
а личное состояние в feed/detail/counters изолировано по проверенному пользователю.
Истории лайков и публичного списка поставивших лайк нет.

Перед deployment US6 повторно примените `ops/database/permissions.sql`: runtime
нужны SELECT/INSERT/DELETE на `engagement.likes`; UPDATE и права Operator на
Engagement отсутствуют. Новых миграций нет. Проверки двух hosts, 100 одновременных
PUT одной пары, смешанных операций 100 пользователей, гонок с закрытием и откатов
сохраняют отчёты `artifacts/validation/us6-likes.json`, `us6-concurrency.md` и
`queries/us6-reads.json`.

Явный bodyless `POST /api/v1/publications/{id}/views` регистрирует открытие Published
материала с неподвижным окном 24 часа. Анонимный потребитель сохраняет выданный
`anonymousVisitorId` и передаёт его в `X-Anonymous-Visitor`. Проверенный Bearer имеет
приоритет на всех устройствах, без объединения анонимной истории. GET ничего не
записывает. Две реплики используют общую БД и visitor HMAC key ring; очистка каждые
15 минут удаляет до 500 истёкших dedup-записей, сохраняя накопленный счётчик.
Перед deployment примените AddViewDeduplication и повторите permissions.sql.
Настройка, ротация и ограничения отката: [anonymous-visitors.md](docs/operations/anonymous-visitors.md).

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
`artifacts/`. Интеграционный runner запускает максимум четыре test collections
одновременно, ограничивая число стартующих PostgreSQL-контейнеров на многоядерных
машинах. Внутри concurrency-сценариев сохраняются все 100 одновременных запросов.
Последовательные подключения используют pool изолированной БД; одновременные
DbContext получают разные PostgreSQL connections. Pool закрывается при удалении БД.
На машине с ограниченными правами Docker-тестам нужен разрешённый
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
Создание principals и применение ограниченных grants описаны в
[`database.md`](docs/operations/database.md); скрипт для Accounts/Publishing/Discussions/likes-таблиц —
[`permissions.sql`](ops/database/permissions.sql). Operator читает Accounts, меняет
только назначения и добавляет аудит; API не имеет доступа к аудиту/записи ролей.

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
| API, учёт просмотров (обязательно) | `AnonymousVisitors:ActiveKeyId`, `AnonymousVisitors:Keys:<keyId>` |
| API, публичный host | `AllowedHosts` (по умолчанию только localhost/127.0.0.1) |
| Operator | `ConnectionStrings:Blog`, `Identity:Issuer` |
| Миграционный процесс | Отдельный DDL connection string из защищённой конфигурации |

Для env используйте `__` вместо `:`, например `Identity__Issuer`.
Реальные issuer/audience/JWKS URL предоставляет оператор установки; production JWKS
использует HTTPS. Visitor HMAC key ring не является RSA-ключом Identity.
Обязательные параметры соответствующего host, включая visitor key ring API, проверяются при запуске.
Не указывайте секреты в исходниках, Git, логах, истории shell или отчётах.

### Изолированные фикстуры

`PostgresFixture` создаёт одноразовый контейнер, а для каждого сценария — отдельную
БД, применяет переданные EF migrations и удаляет БД после проверки. Каждый DbContext
создаётся отдельно; общие изменяемые данные между тестами отсутствуют. Setup
проверяет механизм собственной test-only миграцией `FixtureProbe`; Foundational
использует production `BlogDbContext` и `InitialAccounts`. `EnsureCreated`
не используется.

`ApiFactory` запускает реальный API host; `JwksFixture` предоставляет loopback HTTP
JWKS server, одноразовые RSA/JWT, ротацию и управляемые ответы без живого Identity.
HTTP-тесты используют production JwtBearer и async JWKS adapter. Protected probes
существуют только в тестовом host, без добавления `/me` до US3. `AdjustableTimeProvider` управляет наблюдаемым
UTC без ожиданий, `ConcurrencyBarrier` синхронизирует операции с ограниченным
таймаутом и отменой. Подход основан на документации
[Testcontainers PostgreSQL](https://dotnet.testcontainers.org/modules/postgres/) и
[ASP.NET Core integration testing](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0).

JWKS загружается с единственного настроенного URL без redirects: максимум 256 KiB
после декодирования и 32 публичных RSA RS256 ключа. Freshness не превышает 300 секунд;
меньший max-age соблюдается. Параллельная загрузка объединяется, попытки разделены
30 секундами даже после отказа, общий timeout — 5 секунд. Необходимые недоступные
ключи дают безопасный 503; непригодный JWT — 401, без анонимного fallback.
Локальные роли перечитываются для каждой операции. Reader не хранится в таблице.
Реальный Identity не требуется тестам и не проверен этими фикстурами.
Ограничение readiness внутренним ingress и восстановление зависимостей описаны в
[`health.md`](docs/operations/health.md).

Изолированная PostgreSQL-фикстура использует одноразовые парольные credentials и
`GSS Encryption Mode=Disable`, поскольку тестовый сервер не имеет Kerberos.
Это исключает попытки GSS negotiation при открытии соединений в Linux runner.

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
