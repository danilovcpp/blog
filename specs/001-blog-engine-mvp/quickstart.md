# Quickstart и приёмочная проверка Blog Engine MVP

Дата: 2026-10-24. Основание: [spec.md](spec.md), [research.md](research.md),
[модель данных](data-model.md), [OpenAPI](contracts/openapi.yaml) и
[эксплуатационный контракт](contracts/operations.md).

## Статус и предпосылки

Сейчас репозиторий содержит документы проектирования. Приведённые ниже solution,
проекты, compose-файл, тестовые категории и helper scripts — **контракт будущей
реализации**, который должен войти в `tasks.md`; они пока не созданы и эти команды
не заявлены как выполненные. Руководство исполняют после их реализации.

Локально подтверждены .NET SDK 10.0.402 и ASP.NET Core runtime 10.0.13. Docker CLI
доступен, но чтение engine `desktop-linux` вернуло `permission denied` для pipe
Docker Desktop. Успешный запуск контейнера, PostgreSQL и интеграционных тестов не
подтверждён. Перед проверкой нужен доступ к работающему Linux container engine.
Это не мешает подготовке документов, но блокирует заявление об успешной приёмке кода.

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

Будущий `compose.yaml` запускает только локальный PostgreSQL и получает bootstrap
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

`Operations` проверяет liveness/readiness с отключённой БД и истёкшим JWKS,
восстановление по bounded fetch, запрет production startup migrations,
разделение прав БД, startup validation, cancellation/timeout, graceful stop
cleanup-worker и очистку порциями 500. Отмену не выдают за внутреннюю ошибку;
unexpected exception логируется один раз с traceId и безопасным ProblemDetails.

## Нагрузочная приёмка SC-005

Используется выделенный Linux x64 стенд из R-06: API 4 vCPU/8 GiB,
PostgreSQL 8 vCPU/16 GiB/NVMe SSD, генератор 2 vCPU/4 GiB, сеть ≥1 Gbit/s;
одна API-реплика, никаких посторонних нагрузок. Это фиксированная конфигурация
приёмки, а не утверждение о доступном сейчас оборудовании.

Планируемый console harness `tests/BlogEngine.Performance` и wrapper
`scripts/performance/Invoke-ReadBenchmark.ps1` поддерживают `Seed` и `Run`:

```powershell
$env:BLOG_BENCHMARK_SEED_CONNECTION = Read-Host 'Seed DML connection string только одноразового benchmark PostgreSQL'
$env:BLOG_BENCHMARK_API_URL = Read-Host 'URL API выделенного benchmark стенда'
$env:BLOG_BENCHMARK_FIXTURE_ISSUER = Read-Host 'Фиксированный issuer изолированной benchmark JWKS fixture'
$env:BLOG_BENCHMARK_FIXTURE_AUDIENCE = Read-Host 'Фиксированный audience этой fixture'
$env:BLOG_BENCHMARK_FIXTURE_JWKS_URL = Read-Host 'Доверенный адрес публичного JWKS listener этой fixture'
try {
    pwsh -File scripts/performance/Invoke-ReadBenchmark.ps1 -Mode Seed -Seed 20261024 -OutputDirectory artifacts/performance/sc-005
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark seed не проверен' }
    pwsh -File scripts/performance/Invoke-ReadBenchmark.ps1 -Mode Run -Seed 20261024 -OutputDirectory artifacts/performance/sc-005
    if ($LASTEXITCODE -ne 0) { throw 'SC-005 не выполнен' }
}
finally {
    Remove-Item Env:BLOG_BENCHMARK_SEED_CONNECTION -ErrorAction SilentlyContinue
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

**Контракт Seed**: детерминированно создать и проверить 100 000 Published,
1 000 000 комментариев, 1 000 000 уникальных лайков и aggregate 10 000 000
просмотров; справочники, назначения и пользователи имеют воспроизводимый seed,
разные даты, ties и распределение популярности. Просмотры агрегированы, seed не
имитирует 10 млн сетевых POST и не создаёт бессмысленный журнал всех открытий.
Manifest фиксирует распределения, контрольные количества, SQL checksum/schema,
git revision, фактические SDK/image digests, hardware/OS и настройки PostgreSQL.
Повтор Seed либо проверяет тот же manifest, либо требует явно одноразовую пустую
БД; он не удаляет неизвестную БД и не создаёт скрытый упрощённый путь в API.

**Контракт Run**: 100 closed-loop VU, 2 минуты прогрева вне статистики и 10 минут
измерения; 50% feed / 50% detail, обе сортировки и pageSize 20/100 поровну,
50% feed с фильтрами, ровно 10% запросов с Bearer. Для feed страницы
1/10/100/1000 выбираются равномерно. Набор во время чтения не изменяется.
Harness применяет фиксированный seed выбора запросов, отключает общую раздачу
персонализированных ответов и проверяет correctness выбранных ответов.

Обязательны общий и отдельные отчёты для каждой комбинации
sort × pageSize × наличие фильтров: p95 ≤1 s, p99 ≤2 s, unexpected failures ≤1%.
Медленный вариант не скрывается общей медианой. Сохраняются latency histogram,
RPS/throughput как наблюдение, errors/timeouts, CPU/RAM/I/O, SQL query count и планы,
точный workload manifest и exit code. Незапущенный/укороченный прогон, неподходящее
железо либо любой провал threshold дают неуспешную SC-005. Ограничение write-throughput
горячего материала исследуется отдельно; для него MVP не задаёт выдуманный порог.

## Что приложить к результату реализации

В отчёте фиксируют revision, окружение, команды и фактические outcomes: сборка,
форматирование/анализаторы, unit/API/persistence/Operator, все SC-001–008,
migration SQL/empty/upgrade/recovery, query inspection и полный SC-005.
Отчёты не содержат секретов. Сбой обязательного инструмента или непроверенный
сценарий обозначают как блокировку завершения реализации; текущий план такого
успешного результата не заявляет.
