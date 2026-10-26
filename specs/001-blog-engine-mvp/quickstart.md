# Quickstart и приёмочная проверка Blog Engine MVP

Дата: 2026-10-24. Основание: [spec.md](spec.md), [research.md](research.md),
[модель данных](data-model.md), [OpenAPI](contracts/openapi.yaml) и
[эксплуатационный контракт](contracts/operations.md).

## Статус и предпосылки

Solution, API/Operator, миграции, compose, тестовые категории и helper scripts
реализованы. Фактические исходы команд и оставшиеся gates зафиксированы в
[implementation-report.md](implementation-report.md). Локальный Docker доступен
при разрешённом доступе к engine; тесты используют настоящий PostgreSQL 18.6.
SC-005 исключён из обязательной приёмки текущего этапа/релиза 1.0.0 решением
владельца от 2026-10-24. T121 закрыт без нагрузочного прогона; p95/p99 не подтверждены.

Нужны PowerShell 7, Git, SDK из `global.json`, доступ к NuGet при первом restore,
Docker Compose и возможность получить закреплённый `postgres:18.6` с digest,
зафиксированным реализацией. Production и тесты: PostgreSQL major 18, UTC,
collation `C`, без дополнительных расширений. Данные приёмки полностью одноразовые.
Реальные production-данные, ключи и токены не используются.

Автоматические API-тесты создают RSA/JWKS fixture и JWT в памяти внутри
`WebApplicationFactory`, проходят настоящий JWT middleware и не требуют доступного
Identity. Для ручного запуска оператор предоставляет точные `Identity:Issuer`,
`Identity:Audience`, `Identity:JwksUrl` своего тестового окружения. Значения из
иллюстраций Identity не являются настройками. Проверка такого внешнего окружения
отдельна от автоматических тестов; успешное подключение сейчас не заявлено.

## Восстановление и проверки сборки

Из корня репозитория:

```powershell
dotnet --version
docker version
dotnet tool restore
dotnet restore BlogEngine.slnx --locked-mode
dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror
dotnet format BlogEngine.slnx --verify-no-changes --no-restore
dotnet test tests/BlogEngine.UnitTests/BlogEngine.UnitTests.csproj -c Release --no-build
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build
```

Ожидаются SDK 10.0.402, доступный Docker server, exit code 0 без новых предупреждений,
успешные unit/integration-тесты без пропусков. VSTest выбран явно в `global.json`;
используются xunit.v3 4.0.2, adapter 4.0.1 и Microsoft.NET.Test.Sdk 18.11.0.
Тесты БД используют Testcontainers.PostgreSql 4.16.0 и PostgreSQL 18.6, применяют
миграции и изолируют данные между тестами. Отказ Docker/БД и пропущенный тест
сохраняются как неуспешная проверка, а не как зелёный результат.

## Локальный API и отдельные права БД

`compose.yaml` запускает только локальный PostgreSQL и получает bootstrap
credentials через защищённое окружение оператора; секреты не записываются в Git.
Миграционная учётная запись имеет DDL, API — только необходимый DML и SELECT
повышенных ролей, Operator — ограниченную запись назначений и аудита без DDL.
Руководство предполагает предварительное создание этих principals согласно
[operations.md](contracts/operations.md); API не использует bootstrap/superuser.

В Development используются стандартные user-secrets с разными `UserSecretsId`
для API и Operator. Следующие команды запрашивают значения у оператора, вместо
подстановки выдуманных connection strings и Identity endpoints:

```powershell
docker compose up -d postgres
dotnet user-secrets set 'ConnectionStrings:Blog' (Read-Host 'API connection string, без DDL') --project src/BlogEngine.Api
dotnet user-secrets set 'Identity:Issuer' (Read-Host 'Точный issuer тестового Identity') --project src/BlogEngine.Api
dotnet user-secrets set 'Identity:Audience' (Read-Host 'Точный audience тестового Identity') --project src/BlogEngine.Api
dotnet user-secrets set 'Identity:JwksUrl' (Read-Host 'Фиксированный доверенный JWKS URL') --project src/BlogEngine.Api
$visitorKeyId = Read-Host 'ID visitor HMAC ключа: 1–32 символа A–Z, a–z, 0–9, _ или -'
$visitorKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set 'AnonymousVisitors:ActiveKeyId' $visitorKeyId --project src/BlogEngine.Api
dotnet user-secrets set ('AnonymousVisitors:Keys:' + $visitorKeyId) $visitorKey --project src/BlogEngine.Api
Remove-Variable visitorKey
dotnet user-secrets set 'ConnectionStrings:Blog' (Read-Host 'Operator connection string, без DDL') --project src/BlogEngine.Operator
dotnet user-secrets set 'Identity:Issuer' (Read-Host 'Точный issuer той же установки для Operator') --project src/BlogEngine.Operator
```

`AnonymousVisitors:Keys` — map ID ключа → base64-секрет из 32 случайных байтов;
случайный HMAC-ключ установки не является JWT signing key. Все
API-реплики получают одинаковый key ring. В production обязательны HTTPS для
JWKS, защищённый provider секретов и отсутствие cookie-сессий. User-secrets служит
только локальной разработке. Введённые значения не включают в отчёт проверки.

Миграции выполняются отдельным шагом. Временное окружение имеет приоритет над
user-secrets API; после команды оно удаляется даже при ошибке:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Blog = Read-Host 'Migration connection string с DDL для одноразовой БД'
try {
    dotnet ef migrations has-pending-model-changes --project src/BlogEngine.Infrastructure --startup-project src/BlogEngine.Api
    if ($LASTEXITCODE -ne 0) { throw 'Модель расходится с миграциями' }
    dotnet ef migrations script --idempotent --project src/BlogEngine.Infrastructure --startup-project src/BlogEngine.Api --output artifacts/migrations.sql
    if ($LASTEXITCODE -ne 0) { throw 'Не создан SQL миграций' }
    dotnet ef database update --project src/BlogEngine.Infrastructure --startup-project src/BlogEngine.Api
    if ($LASTEXITCODE -ne 0) { throw 'Миграция не применена' }
}
finally {
    Remove-Item Env:ConnectionStrings__Blog -ErrorAction SilentlyContinue
}
dotnet run --project src/BlogEngine.Api -c Release --no-build --no-launch-profile --urls http://localhost:5080
```

`artifacts/` создаёт реализация. SQL предварительно проверяют на ограничения,
индексы, права и блокировки; production startup не вызывает `Database.Migrate`.
Для команд Operator в другом терминале также задают `DOTNET_ENVIRONMENT=Development`.

```powershell
Invoke-RestMethod http://localhost:5080/health/live
Invoke-RestMethod http://localhost:5080/health/ready
Invoke-RestMethod http://localhost:5080/openapi/v1.json
```

Ожидается 200 для live; ready даёт 200 при доступной БД и пригодном JWKS snapshot,
503 при недоступной критической зависимости. Liveness не зависит от Identity/БД.
OpenAPI 3.1 и `/swagger` доступны в Development. Ошибка запуска обязательной
конфигурации — отказ старта; пустые настройки не подменяются permissive defaults.

## Оператор и ручная работа с версиями

Сначала проверенный пользователь вызывает `GET /api/v1/me` своим тестовым Bearer:
создаётся одна локальная связь с Reader. Оператор находит уже существующую связь
и использует выданный локальный UUID. Синтаксис и exit codes нормативно описаны
в [operations.md](contracts/operations.md).

```powershell
$subject = Read-Host 'Проверенный UUID sub пользователя тестового Identity'
$issuer = Read-Host 'Точный issuer той же установки'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- users find --issuer $issuer --subject $subject
$localUserId = Read-Host 'UUID найденного локального пользователя'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles list --user-id $localUserId
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles grant --user-id $localUserId --role Author --reason 'MVP acceptance: author fixture'
dotnet run --project src/BlogEngine.Operator -c Release --no-build -- roles revoke --user-id $localUserId --role Author --reason 'MVP acceptance: revoke fixture'
```

Grant/revoke успешно фиксируют `RoleChangeAudit`; Reader не выдаётся/отзывается
как повышенная роль. Следующая HTTP-операция после commit использует новые права
с тем же ещё действительным JWT. Первый Admin назначается тем же средством;
HTTP-запросы не управляют ролями, Operator не меняет каталог Identity.

При ручной проверке изменяющих endpoints получают текущую `version` из DTO и
передают её UUID в `X-Resource-Version`. После содержательной записи используют
новую version. Отсутствующая/неверная версия — 400, устаревшая —
409 `concurrency_conflict`; лайки и просмотры edit-version не меняют.
Набор маршрутов, JSON и ограничения берут из OpenAPI; EF entities не являются DTO.
Markdown проверяется по декодированному UTF-8 размеру 1 MiB, общий JSON body имеет
предел 8 MiB с ProblemDetails 413 `request_too_large`, чтобы JSON escaping допустимого
контента сам по себе не нарушал лимит Markdown.

## Воспроизводимые функциональные сценарии

Каждая строка ниже — обязательная категория тестов будущей реализации.
Например, сценарий авторизации запускается без реального Identity:

```powershell
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter 'Category=Authentication'
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter 'Category=Concurrency'
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter 'Category=ConsumerContract'
```

Для остальных сценариев заменяют значение `Category`. Каждый сценарий создаёт
свои данные и возвращает ненулевой exit code при нарушении ожидаемого результата.

| Категория / покрытие | Действия и проверяемый результат |
|---|---|
| `Publishing` — US1, SC-001 | Author создаёт/исправляет Draft, Admin работает с чужим материалом; Reader и чужой Author не меняют данные. Пустой Markdown допустим до publish; publish назначает первую дату, прямое редактирование Published сразу публично. Unpublish закрывает материал, republish сохраняет первую дату и slug. Delete терминален, slug не переиспользуется; stale version не перезаписывает данные. Admin управляет справочниками; назначение ограничено 5/20, rename виден в DTO, удаление используемой записи даёт конфликт. |
| `Reading` — US2, SC-002/006 | Лента отдаёт только Published с детерминированным tie-breaker. Категории OR и теги AND; неизвестные фильтры — 400, существующие без совпадений — пустой ответ. Набор 250 материалов обходится без повторов/пропусков; оба sort, размеры 20/100, пустая следующая страница. ID/slug совпадают, feed не содержит полный Markdown, detail содержит. Draft/deleted через все публичные маршруты дают скрывающий 404. |
| `Authorization` — US3, SC-007 | 100 одновременных первых проверенных обращений создают одного LocalUser с Reader. Полная матрица роль × действие × владение, совмещение Author/Moderator и Admin; HTTP-подмена роли/owner запрещена. Operator grant/revoke меняет право со следующей операции; API DB principal не может назначить роль. |
| `Authentication` — US3, SC-007 | Настоящий Bearer middleware: RS256 и корректные claims, границы времени/skew; чужие issuer/audience, duplicate/missing claims, неверные UUID/тип/signature/token_use, opaque refresh token и JWT с неверным token_use и oversized JWT отклоняются. Плохой Bearer на публичном персонализируемом запросе даёт 401. JWKS: fixed URL, отсутствие redirect и влияния jku/x5u, single-flight, 30 s cooldown, timeout 5 s, срок snapshot ≤300 s, ротация/удаление kid и fail-closed 401/503; время и ответы контролируемы без sleep. |
| `Discussions` — US4, SC-002/006 | Reader создаёт/исправляет Visible без предварительной очереди; чужой Reader не редактирует. Parent того же материала, глубина ≤5; cross-post parent, изменение parent и ответ на скрытый/удалённый узел запрещены. Delete необратимо убирает текст; видимые потомки сохраняются через заглушки без текста/автора. 250 корней с 250 прямыми ответами обходятся отдельными ограниченными страницами без пропусков/повторов; проверяются и вложенные ветви. |
| `Moderation` — US5, SC-002/007 | Moderator видит список, скрывает/восстанавливает; скрытое не раскрывается публично. Автор скрытого не редактирует и не восстанавливает, но удаляет навсегда. Moderator не переписывает чужие публикации/комментарии; Admin может изменить/удалить. Deleted текст не восстанавливается. |
| `Likes` — US6, SC-003 | 100 конкурентных PUT одного пользователя оставляют один лайк; 100 разных пользователей с конкурентными постановками/снятиями дают счётчик, равный реальным уникальным строкам. Повторный DELETE успешен, счётчик ≥0; персональное состояние корректно, общая кеш-доставка персонального ответа исключена. Popularity сортирует likes/первую дату/ID по убыванию с действующими фильтрами. |
| `Views` — US7, SC-004 | 100 конкурентных открытий одной пары дают +1 для Bearer и анонимного token. Повторы до T+24 h не сдвигают T, точная граница даёт ровно +1. Другой посетитель независим, смена устройства Bearer сохраняет личность; вход не объединяет анонимную историю. Потеря/подделка visitor token выдаёт новый, чтение без POST не меняет счётчик. Cleanup не уменьшает aggregate и не открывает повтор до границы. |
| `Concurrency` — SC-001/003/004/007 | Барьеры, отдельные connections/contexts и два API instance: slug creation, user bootstrap, stale writes, assignment/delete taxonomy, publish close/delete против comments/likes/views. Порядок блокировок согласован; итог не содержит публичных взаимодействий закрытого материала, сирот или отрицательных/лишних счётчиков. Injected failure между child-write и counter-update откатывает обе части; повтор после rollback успешен. |
| `ConsumerContract` — SC-008 | Потребитель только по OpenAPI и operations.md проходит US1–7. Все ошибки имеют application/problem+json, стабильный code и traceId, validation errors по полям; проверяются 400/401/403/404/409/413/503. Неизвестные поля, лимиты строк/байтов/страниц/обложки отклоняются. Диагностика и общедоступные DTO не содержат access/refresh tokens, stack trace, SQL или скрытого текста. Visitor token возвращается только намеренно в ViewResult; Hidden-текст доступен только в разрешённых Admin/moderation DTO, никогда в публичной заглушке или owner Hidden edit-context. |

Unit-набор отдельно проверяет те же бизнес-границы без ASP.NET/EF/БД, включая
публикацию пустого текста, фиксацию slug и первой даты, состояния комментария и
предел глубины. HTTP/persistence тесты доказывают ограничения реального PostgreSQL.

## Миграции, ограниченность запросов и отказоустойчивость

Планируемые helper scripts запускают соответствующие фикстуры и сохраняют
машиночитаемый отчёт в `artifacts/validation/`, без credentials:

```powershell
pwsh -File scripts/validation/Invoke-MigrationValidation.ps1 -OutputDirectory artifacts/validation/migrations
pwsh -File scripts/validation/Invoke-QueryInspection.ps1 -OutputDirectory artifacts/validation/queries
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter 'Category=Operations'
```

`Invoke-MigrationValidation.ps1` создаёт отдельные одноразовые PostgreSQL БД:
применяет всю цепочку с нуля; восстанавливает предыдущую schema baseline с
репрезентативными данными и обновляет её; сверяет данные, constraints/indexes и
отсутствие pending model changes; проверяет повтор idempotent SQL. Для первой
миграции baseline — пустая схема, а не выдуманная старая версия. Для будущих
разрушающих миграций тестирует документированный перенос и восстановление через
rollback либо исправляющую миграцию на одноразовой копии. Не откатывает рабочую БД.

`Invoke-QueryInspection.ps1` сохраняет параметризованный SQL, число команд,
серверный LIMIT/DTO projection и `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` для обеих
сортировок, pageSize 20/100, page 1/10/100/1000 и вариантов фильтров. Feed имеет
≤5 data queries независимо от размера страницы; фиксированные auth/bootstrap
запросы учитываются отдельно. Проверяется отсутствие N+1/lazy loading/полного
Markdown в карточках; конкретная форма planner node не прибивается хрупким assert.
Для feed инспекция включает category-only/tag-only/combined с 1 и предельными
5/20 ID, группы p/m/r и фактическую cardinality. Проверяются возможные непустые
варианты и обязательные пустые; 20 тегов на `sc005-v1` всегда дают пустой результат,
поскольку материал имеет пять тегов. Невозможные непустые сочетания не выдумываются.

**Обязательная проверка списков categories/tags (T043 → T120).** На основном
`sc005-v1` проверяются все 200 categories/2 000 tags. Дополнительно на отдельной
одноразовой копии увеличиваются только справочники до 2 000/20 000 записей,
сохраняя существующие назначения и добавляя неназначенные записи; эта копия
не используется для SC-005. Имена включают совпадения для проверки tie-breaker.
После seed выполняется ANALYZE. Для каждого списка нужны default page,
pageSize 20/100, первая/средняя/последняя/следующая пустая страницы на обоих объёмах.

Фиксируется SQL, реально исполненный HTTP/persistence-путём приложения, с типами
параметров и границами data-команд отдельно от auth/bootstrap; переписанный вручную
«эквивалентный» SQL не заменяет его. Критерии прохождения:

- Ровно один data SELECT на запрос списка независимо от размера страницы/объёма;
  нет N+1, lazy loading, отдельного COUNT или запросов на каждую запись.
- ORDER BY name COLLATE C, id и offset/LIMIT выполняются в PostgreSQL;
  из reader материализуется не более pageSize+1 строк, в DTO не более pageSize.
  Проекция содержит только id/name/slug/version; нет EF-графов, связанного контента,
  отслеживания сущностей или клиентской фильтрации/сортировки растущей выборки.
- На фактическом SQL сохраняется `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`:
  estimates/actual rows, loops, buffers, scan/sort, время и временный I/O.
  Подтверждается наличие/совместимость индекса (name, id) с collation/order,
  фиксируются реально выбранные индексы и обоснование плана на обоих объёмах.
  Seq Scan на небольшом справочнике или глубокой странице сам по себе не провал;
  обязательны объяснение измеренной стоимости и отсутствия ошибки запроса/индекса.
  Принудительное выключение Seq Scan не доказывает пригодность production-плана.
- Нет потерянных/дублированных элементов при одинаковых именах на неизменном наборе;
  SQL count, лимит reader/DTO, порядок и состав полей проверены автоматически.
  Отсутствие любого обязательного плана/замера/обоснования означает непрохождение.

Отчёт `artifacts/validation/queries/taxonomy/` содержит revision/schema checksum,
объём и статистику данных, запрос/параметры, число команд/материализованных строк,
проекцию, JSON-планы/использованные индексы, вывод по каждому критерию pass/fail.
Измеренно необходимый индекс оформляется EF migration с повторной проверкой,
не ручным DDL или хрупким требованием конкретного planner node. Для справочников
не вводится отдельный неподтверждённый latency-порог; обязательны эти проверки
конституции III, а численные цели SC-005 относятся к ленте/материалу.

`Operations` проверяет liveness/readiness с отключённой БД и истёкшим JWKS,
восстановление по bounded fetch, запрет production startup migrations,
разделение прав БД, startup validation, cancellation/timeout, graceful stop
cleanup-worker и очистку порциями 500. Отмену не выдают за внутреннюю ошибку;
unexpected exception логируется один раз с traceId и безопасным ProblemDetails.

## Нагрузочная приёмка SC-005

Этот раздел необязателен для текущего этапа/релиза 1.0.0 по решению владельца
от 2026-10-24. Он сохранён для возможного будущего прогона; отсутствие прогона
не блокирует текущую приёмку. Критерии успеха самого benchmark не изменены.

Используется выделенный Linux x64 стенд из R-06: API 4 vCPU/8 GiB,
PostgreSQL 8 vCPU/16 GiB/NVMe SSD, генератор 2 vCPU/4 GiB, сеть ≥1 Gbit/s;
одна API-реплика, никаких посторонних нагрузок. Это фиксированная конфигурация
приёмки, а не утверждение о доступном сейчас оборудовании.

Планируемый console harness `tests/BlogEngine.Performance` и wrapper
`scripts/performance/Invoke-ReadBenchmark.ps1` поддерживают `Seed` и `Run`:

```powershell
$env:BLOG_BENCHMARK_SEED_CONNECTION = Read-Host 'Seed DML+MAINTAIN connection string только одноразового benchmark PostgreSQL'
$env:BLOG_BENCHMARK_POSTGRES_IMAGE = Read-Host 'Фактический образ PG18.6 с проверенным @sha256 digest'
$env:BLOG_BENCHMARK_DISPOSABLE_DATABASE = Read-Host 'Точное имя пустой sc005_disposable_ БД, предварительно мигрированной оператором'
$env:BLOG_BENCHMARK_API_URL = Read-Host 'URL API выделенного benchmark стенда'
$env:BLOG_BENCHMARK_FIXTURE_ISSUER = Read-Host 'Фиксированный issuer изолированной benchmark JWKS fixture'
$env:BLOG_BENCHMARK_FIXTURE_AUDIENCE = Read-Host 'Фиксированный audience этой fixture'
$env:BLOG_BENCHMARK_FIXTURE_JWKS_URL = Read-Host 'Доверенный адрес публичного JWKS listener этой fixture'
$env:BLOG_BENCHMARK_FIXTURE_LISTEN_URL = Read-Host 'Локальный URL привязки listener; public JWKS URL доступен API через HTTPS ingress'
try {
    pwsh -File scripts/performance/Invoke-ReadBenchmark.ps1 -Mode Seed -Seed 20261024 -OutputDirectory artifacts/performance/sc-005
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark seed не проверен' }
    pwsh -File scripts/performance/Invoke-ReadBenchmark.ps1 -Mode Run -Seed 20261024 -OutputDirectory artifacts/performance/sc-005
    if ($LASTEXITCODE -ne 0) { throw 'SC-005 не выполнен' }
}
finally {
    Remove-Item Env:BLOG_BENCHMARK_SEED_CONNECTION -ErrorAction SilentlyContinue
    Remove-Item Env:BLOG_BENCHMARK_DISPOSABLE_DATABASE -ErrorAction SilentlyContinue
}
```

Оператор предварительно мигрирует одноразовую БД и настраивает API на ровно эти
публичные issuer/audience/JWKS URL fixture; production-настройки не изменяются.
Run запускает изолированный JWKS listener внутри performance harness, создаёт
одноразовые RSA keys и JWT только в памяти, ждёт readiness API, затем начинает
нагрузку. Это тестовая инфраструктура, не endpoint выдачи токенов Blog Engine и
не отдельный production host. В отчёт входят только публичные fixture settings;
ключи/JWT/connection strings не выводятся. Нужные роли и LocalUser подготовлены
seed. Для нагрузочного чтения JWKS refresh не зависит от живого Identity.

Seed требует точного совпадения имени с `BLOG_BENCHMARK_DISPOSABLE_DATABASE`
и префикса `sc005_disposable_`. Непустая БД принимается только с прежним
`seed-manifest.json`, совпадающими canonical checksums и schema/corpus/issuer.
Неизвестные данные не удаляются. Все пути отчётов передаются без секретов.
Seed principal имеет DML, SELECT migration history и MAINTAIN для ANALYZE
одноразовых таблиц. API credential для этой операции не используется.
`-Diagnostic` у wrapper выполняет короткую техническую проверку (1 s + 2 s),
всегда возвращает ненулевой exit code и не подтверждает SC-005.

До приёмочного Run разместите в OutputDirectory `stand-evidence.json`.
Поля `api`, `postgres`, `generator` содержат `os="linux-x64"`, точные `vCpu`
4/8/2 и `memoryGiB` 8/16/4 соответственно, `dedicated=true`, реальный `imageDigest`
и массив `metrics` с не менее 600 секундными замерами `at` (UTC), `cpuSeconds`, `residentBytes`,
`ioBytes`. Дополнительно нужны `postgres.nvme=true`, `networkGbps>=1`,
`apiReplicas=1`, `noOtherLoad=true`. Сбор этих показателей на выделенных hosts
выполняет мониторинг стенда; generator также сохраняет собственные process/proc
замеры. Отсутствующая/неподходящая evidence запрещает exit 0. Файл не должен
содержать credentials, tokens или environment dump. Это проверяемое evidence
окружения, его нельзя заполнять предположениями о локальном Docker.
Wrapper копирует SQL evidence из `-QueryEvidenceDirectory` (по умолчанию
`artifacts/validation/queries`) в `OutputDirectory/queries`. Run проверяет
полноту/hash файлов SQL/планов, status, canonical data/schema checksums и PG settings
против seed manifest. Evidence другой схемы/данных/настроек или отсутствующие планы
запрещают exit 0. Для приёмки планы собирают с проверяемыми настройками PostgreSQL.

Полный seed и query inspection исполняются автоматическим тестом
`FullDatasetQueryTests`: он создаёт одноразовую БД и отдельную expanded copy,
проверяет повторную генерацию и удаляет только свои fixture БД. Для этого нужен
свободный диск под несколько GiB данных и время до 20 минут. Команды:

```powershell
pwsh -File scripts/validation/Invoke-MigrationValidation.ps1
pwsh -File scripts/validation/Invoke-QueryInspection.ps1
dotnet test tests/BlogEngine.IntegrationTests -c Release --filter 'FullyQualifiedName~ConsumerWalkthroughTests|FullyQualifiedName~OpenApiCompatibilityTests'
```

**Контракт Seed**: реализовать нормативный
[профиль SC-005](spec.md#приёмочный-профиль-sc-005) `sc005-v1`: 100 000 Published,
1 000 000 comments, 1 000 000 unique likes, aggregate 10 000 000 views,
Markdown 70/25/5% по 2/16/128 KiB и все частоты categories/tags.
Seed работает только с явно одноразовой пустой БД либо проверяет прежний manifest;
не удаляет неизвестную БД и не имитирует 10 млн HTTP открытий.

**Детерминированная генерация T118**

- Ординалы публикаций `n=0..99999`. `H(namespace,n,counter)` — SHA-256 UTF-8 строки
  `sc005-v1|20261024|namespace|n|counter`, числа десятичные invariant, без пробелов,
  counter по умолчанию 0. Перестановка `P_namespace` — сортировка всех ординалов
  по байтам H лексикографически, при равенстве по n; `r_namespace(n)` — её ранг с 0.
  Это не runtime-specific `System.Random`, `GetHashCode` или текущая дата.
- UUID выводятся из H с отдельными namespace сущности/ordinal: первые 16 bytes
  в сетевом порядке, version/variant bits UUIDv8; UUID сравниваются штатно в PG.
  Автор задаётся `r_authors(n) mod 1000`; размеры — рангами `sizes`:
  первые 70 000 short, следующие 25 000 medium, последние 5 000 long.
  Текст из версионируемого UTF-8 корпуса слов с SHA-256 в manifest выбирается
  байтами H(`body`,n,counter), с фиксированными Markdown separators/LF;
  до точного размера дополняется на границе Unicode scalar ASCII-символами.
  Корпус/правила входят в исходники генератора v1; одинаковый корпус и версия
  дают одинаковые bytes. Нет одинаковой строки-заполнителя для всех материалов.
- Одна hot-category: `r_cat-hot(n) mod 10`. Для второй `r=r_cat-secondary(n)`:
  если `r mod 5 < 4`, medium ID `(4*floor(r/5)+(r mod 5)) mod 40`;
  иначе rare ID `floor(r/5) mod 150`. ID внутри групп различны.
- Для двух hot-tags `r=r_tag-hot(n)`: ID `r mod 20` и
  `(r+1+(floor(r/20) mod 19)) mod 20`. Для остальных трёх `j=3*r_tag-secondary(n)+s`,
  `s=0,1,2`: если `j mod 5 < 3`, medium ID
  `(3*floor(j/5)+(j mod 5)) mod 180`; иначе rare ID
  `(2*floor(j/5)+(j mod 5)-3) mod 1800`. Это даёт точные частоты профиля
  без дублей внутри материала и без перебора случайных выборок до совпадения.
- `r_likes` и `r_comments` независимы: границы рангов 20 000/80 000/95 000/100 000
  задают 0/5/20/80 соответствующих строк. Для likes пользователи выбираются
  циклически от H-derived offset без повторов у материала. Comments разбиваются
  на группы по 5: корень и четыре прямых ответа; ранги `comment-state` всех
  миллиона comment ordinals задают 900 000 Visible, 50 000 Hidden, 50 000 Deleted.
  Текст Visible/Hidden — 512 UTF-8 bytes недоверенного plain text, Deleted — null;
  ancestor counts вычисляются из итоговых состояний, а не задаются произвольно.
- Временная база `2026-10-01T00:00:00Z`: firstPublishedAt =
  база − `30*floor(r_dates(n)/10)` минут (ties по 10 материалов),
  createdAt = firstPublishedAt −24 h, updatedAt = firstPublishedAt.
  Даты комментариев/лайков — последовательные минуты после первой публикации;
  остальные системные поля тоже выводятся из этой базы/ординалов. Исторические
  просмотры агрегированы по формуле профиля; active dedup пуст.
- Manifest хранит профиль/seed/корпус и версию генератора, точные counts,
  UTF-8 histogram, частоты каждого ID и суммы joins/counters, canonical SHA-256
  строк всех таблиц (по PK, UTF-8/LF, invariant UTC/числа, явные null),
  schema SQL checksum, git revision, SDK/image digests, hardware/OS/PG settings.
  После seed выполняется ANALYZE. Повторная независимая генерация обязана дать
  те же data checksums; окружение/JWT/время запуска не входят в data checksum.

**Детерминированный план T119**

Квоты задаются полными блоками по 400 запросов: 200 feed и 200 detail.
В detail — 140/50/10 short/medium/long, внутри каждой группы ID/slug поровну.
В feed — 100 unfiltered и 100 filtered; в каждой сотне по 25 запросов страниц
1/10/100/1000. Для каждой сотни индекс слота `i mod 4` задаёт комбинации
recent/20, recent/100, popular/20, popular/100: по 25 на комбинацию.
Bearer назначается перестановкой `request-auth` ровно 20 feed и 20 detail
слотам. Это сохраняет все согласованные исходные пропорции.

Ниже — воспроизводимая раскладка 100 filtered слотов. C = category-only,
T = tag-only, CT = categories+tags; p/m/r = popular/medium/rare по самому
редкому выбранному ID, число после `=` — количество запросов.
Внутри страницы сначала идут непустые, затем zero, затем beyond;
внутри клетки порядок C/T/CT, затем p/m/r.

| page | Непустые | Пустые: нулевое пересечение (zero) | Пустые: после последней (beyond) |
|---|---|---|---|
| 1 | C(p)=6, C(r)=1; T(p)=1, T(m)=1, T(r)=1 | CT(m)=8, CT(r)=7 | — |
| 10 | C(m)=8; T(m)=2; CT(p)=10 | T(m)=3, T(r)=2 | — |
| 100 | C(p)=15; T(p)=5 | T(m)=2, T(r)=3 | — |
| 1000 | — | — | C(p)=10, C(m)=5, C(r)=5; T(p)=3, T(m)=1, T(r)=1 |

Итого: C/T/CT =50/25/25; p/m/r =50/30/20; непустые/zero/beyond =50/25/25.
Каталог подходящих ID/пар готовится до замера из фактических назначений:
нулевые пересечения используют существующие теги, обычно ALL двух тегов;
для непустых проверяется `matchedCount > (page-1)*pageSize`, для beyond —
`0 < matchedCount <= (page-1)*pageSize`. Для CT(p) на page 10 выбираются
одна popular category и один popular tag с достаточным пересечением.
Все варианты выбираются по H(`request-choice`, номер блока, слот) из отсортированного
каталога, detail — равномерно из нужной группы. Не используется один и тот же
малый набор материалов/фильтров для всего прогона. Известные ожидаемые ID,
порядок, длина страницы и hasNextPage вычисляются из seed; catalog/template
checksum и последовательность выбранных запросов входят в manifest.
Несовместимые квоты, отсутствие кандидата или неверная cardinality блокируют Run.

**Порядок замера и прохождение**

100 closed-loop VU работают с общим детерминированным порядком слотов, без
искусственной задержки между запросами. Прогрев 2 минуты исключён из статистики;
его незавершённые запросы дренируются до основного окна. Затем счётчики блоков
и статистика сбрасываются: основной план всегда начинается с блока 0 и тем же
seed, независимо от количества запросов, успевших выполниться при прогреве.
Основное окно — полные 10 минут; затем прекращается допуск новых блоков,
последний начатый блок завершается, чтобы не исказить квоты отрезанным хвостом.
Таймаут отдельного HTTP-запроса 5 s; дренаж ограничен 30 s. Окно и дренаж
фиксируются отдельно, все ошибки/медленные ответы/остатки блока учитываются;
недренированный блок означает неуспешный прогон. Пороги проверяются как
для основного окна, так и для всего набора с дренажом, точные квоты — для
полных блоков. Дополнительный дренаж не сокращает согласованные 10 минут.

Клиент измеряет монотонным таймером время от отправки HTTP до полного чтения body;
подготовка seed/catalog/ANALYZE не входит в замер. Набор во время чтения неизменен,
общий cache не раздаёт персонализированные ответы. Сохраняются общий и восемь
feed sort×pageSize×filtered/unfiltered отчётов: p95 ≤1 s, p99 ≤2 s,
unexpected failures ≤1%. Timeout, network error и неожиданный статус входят
в failures со всеми попытками в знаменателе; автоматический retry не прячет сбои.
Для latency timeout учитывается как 5 s, HTTP failures — с измеренной длительностью.
Процентили считаются по всем попыткам, nearest-rank по возрастанию;
измерение до полного body включает стоимость длинного Markdown.

Отдельно сохраняются p50/max/RPS, response bytes, errors/timeouts, CPU/RAM/I/O,
SQL counts/plans и разрезы short/medium/long, p/m/r, full/partial/empty/zero/beyond.
Фактические квоты/селективности и checksums сверяются до/после Run.
Exit 0 допустим только при точном профиле, корректных ответах, всех обязательных
вариантах, пригодном стенде и прохождении всех исходных порогов. Неверный payload,
пропущенный вариант, незапущенный/укороченный замер или провал порога блокируют SC-005.
Ограничение write-throughput горячего материала исследуется отдельно;
для него MVP не задаёт выдуманный порог.

## Что приложить к результату реализации

В отчёте фиксируют revision, окружение, команды и фактические outcomes: сборка,
форматирование/анализаторы, unit/API/persistence/Operator, все SC-001–008,
migration SQL/empty/upgrade/recovery и query inspection. SC-005 указывают отдельно
как отложенный по решению владельца; T121 закрыт без нагрузочной приёмки.
Отчёты не содержат секретов. Сбой обязательного инструмента или непроверенный
обязательный сценарий обозначают как блокировку завершения реализации.
Отдельный smoke с реальным `danilovcpp/identity:latest` прошёл 25 проверок
в одноразовом Development-окружении: запуск/ограничения описаны в
[deployment.md](../../docs/operations/deployment.md#проверка-реального-identity).
Production rollout не заявляется без фактической проверки.
