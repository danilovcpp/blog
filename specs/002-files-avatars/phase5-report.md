# Phase 5 / US3 — собственные аватары

Дата: 2026-11-11. Область: T036–T043. Следующая фаза не выполнялась.

## Реализация

- Любой проверенный пользователь получает собственное состояние через
  `GET /me/avatar` и `GET /me`. Конкурентный bootstrap через `INSERT ON CONFLICT`
  сходится на одной UUID version. `Me` проецирует аватар, его версию и текущие
  локальные роли одним SQL statement. Публичные чтения state не создают.
- Avatar использует существующие init/raw PUT/status/complete/cancel и
  durable processing. Ожидаемая версия обязательна, включена в fingerprint,
  проверяется при новом init, complete/claim и окончательном commit.
  Точные init/Completed replays возвращают исторический результат после
  замены или удаления и не назначают файл повторно.
- JPEG/PNG/WebP проходят общий безопасный codec. Renderer применяет
  ориентацию до центральной обрезки и создаёт PNG 256×256 и 512×512,
  допуская увеличение и сохраняя alpha. Исходные metadata не переносятся.
  Durable plans сохраняются до PUT; network/decode выполняются вне транзакций.
- Финализация следует порядку Present → обе representations → Ready →
  новая current reference/version → Deleted прежнего файла → удаление его
  representation refs → retirement. Новый размер полностью резервируется
  до замены; used меняется ровно на разность исходных размеров. Старый файл
  и исходник нового аватара получают cleanup jobs. Ошибка версии отклоняет
  кандидат, освобождает его резерв и выводит новые объекты в cleanup.
- Versioned DELETE возвращает новое состояние. Пустой DELETE с текущей
  версией — no-op, stale — 409. Пользователь меняет только своё состояние;
  Admin не получает возможности подменить владельца.
- Публичный PNG доступен только по текущему назначению и размеру 256/512.
  После verified spool назначение проверяется заново, в том числе после
  storage fault. Замена/удаление даёт 404 вместо прежнего PNG или storage 503.
  При сохранившемся назначении missing/corrupt object даёт безопасный 503.
  Сохраняются no-store, nosniff, полный Content-Length, Ignore Range и
  проверка optional Bearer. Библиотечные original/DELETE для Avatar дают 404.
- Nullable `AvatarReference` добавлен во все сериализуемые AuthorRef:
  feed/detail/workspace, комментарии, edit context, moderation и команды.
  Командные publication/comment snapshots читают avatar и остальные поля
  одной SQL-проекцией до commit через Media-owned `AuthorAvatarQuery`.
  Public Hidden/Deleted placeholders сохраняют `author:null`.
- Feed получает один batch для максимум 100 различных авторов внутри
  существующего read-only REPEATABLE READ. Максимум data queries с обеими
  проверками фильтров теперь 6; число не зависит от размера страницы.
  Остальные проекции включают avatar subquery в существующий SQL.
- Промежуточный `contracts/openapi-us3.yaml` включает реализованные US1–US3
  и расширенные строгие Me/AuthorRef/Upload schemas. Historical contracts
  сохранены. Полная агрегация и переход production consumers остаются Phase 7.

## Code review и constitution 1.0.0

Проведено самостоятельное review application, persistence, SQL projections,
binary transport, DTO/DI, OpenAPI и тестов. Проверены общий lock order,
fencing, rollback, неизменность исторических результатов, квота и отсутствие
чтений ответа команды после commit. Найденные замечания исправлены:

1. Проекция Media-owned avatar assignment изменена на member initialization,
   чтобы EF корректно компоновал её в коррелированные SQL subqueries.
2. Upload/UploadResult промежуточного OpenAPI теперь разрешают Avatar;
   реальные upload/state/result payloads проходят строгую schema validation.
3. Query budget существующих feed regression tests учитывает один новый
   bounded batch; отдельная проверка требует один SQL для 1 и 100 авторов.

Подтверждено, что под state lock в UUID-порядок files включён прежний аватар
вместе с кандидатом. Это сохраняет порядок при конкурирующих swap/delete.
Открытых замечаний по корректности и сопровождению в пределах Phase 5 нет.

| Принцип | Проверяемое подтверждение в этой фазе |
| --- | --- |
| I — модульность | Avatar lifecycle в Media; Core Publishing/Discussions/ReadModels используют Media.Contracts; нет EF/HTTP/Skia в Core; architecture tests |
| II — C# | Nullable, async/cancellation, явные зависимости и владение spool; build/analyzers/format; нет заглушек и подавлений |
| III — данные | Allowance → state → files по UUID → upload; короткие tx без Garage/decode; существующие FK действуют на каждом flush; coherent SQL/snapshots и bounded queries |
| IV — API/security | Verified owner, обязательные версии, strict DTO, private source, old URL 404, pre-header recheck при storage fault, ProblemDetails/no-store |
| V — тесты | RED до реализации; unit, HTTP/PostgreSQL/Garage, 100-way races, controlled rendering/download/response/read-snapshot barriers, negative cases и регрессия |
| VI — эксплуатация | Durable plans/retry/cleanup jobs, bounded transfer/spool/codec, отмена и безопасные ошибки; Linux runtime validation |

Изменений схемы, grants, зависимостей и package lock files нет.
`.gitignore`/`.dockerignore` проверены; дополнительных ignore-файлов не требуется.
`mockups/` не затронут. Checklist markers сохранены: backend 36/36,
requirements 16/16. `.specify/extensions.yml` отсутствует, hooks не зарегистрированы.

## Проверки

TRX находятся в `artifacts/test-results/`, phase evidence — в
`artifacts/validation/media/phase5/`; оба каталога игнорируются Git.

- RED: 3 новых HTTP-теста воспроизвели отсутствие avatar endpoints и полей Me.
- `dotnet restore BlogEngine.slnx --locked-mode` — PASS.
- `dotnet build BlogEngine.slnx -c Release --no-restore --disable-build-servers -m:1 -warnaserror`
  — PASS, 0 warnings/errors.
- `dotnet format BlogEngine.slnx --verify-no-changes --no-restore` — PASS.
- Первичный расширенный US3/codec/contract suite — 32 passed, 0 failed/skipped.
- Linux `media-codec-validation` в закреплённом ASP.NET runtime,
  `docker run --rm --memory 1g --cpus 2 blog-media-codec-validation:phase5` —
  9 passed, 0 errors/failed/skipped/not-run.
- `dotnet test tests/BlogEngine.UnitTests/BlogEngine.UnitTests.csproj -c Release --no-build`
  — 97 passed, 0 failed/skipped; `phase5-unit-final.trx`.
- `dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter 'Category!=BenchmarkDataset'`
  — 302 passed, 0 failed/skipped; `phase5-integration-final.trx`, 3m41s.
- `concurrency.json`: 100 принятых кандидатов на двух hosts, одно назначение
  и 99 Rejected/concurrency_conflict, один DELETE и 99 version conflicts,
  used после удаления равен нулю. Bootstrap также проверен на двух hosts.
- `avatar-query.json`: 1 data query для 1 и 100 авторов, параметризованный
  SQL и настоящий EXPLAIN ANALYZE/BUFFERS. PostgreSQL использует PK index
  и bitmap scan. Public readers не создают avatar state.
- Проверены замена во время render перед finalization, успешный/неуспешный
  GET во время swap/delete, исторические init/complete после swap/delete,
  полное резервирование нового размера, PNG JPEG/PNG/WebP через HTTP,
  снимки команд после другого commit и feed snapshot между page и avatar batch.

Первый широкий прогон дал 301 pass и один отказ в старом ожидании пяти data
queries в LikeContractTests; ожидание обновлено на шесть с явным avatar batch.
Финальная регрессия прошла полностью. Ранее исправлены ошибка перевода SQL
и неполные purpose enums промежуточного OpenAPI. Неуспешные прогоны не
засчитывались как успешные. Docker/MSBuild потребовали доступа за пределами
sandbox; проверки с разрешённым доступом завершились успешно.

- `dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --filter 'Category=BenchmarkDataset'`
  — 1 passed, 0 failed/skipped; `phase5-benchmark.trx`, 5m09s. Проверены
  повторный canonical seed, 400 основных запросов, SQL/EXPLAIN для обоих
  порядков/фильтров/страниц, diagnostic response contract и taxonomy clone.
  Локальный диагностический запуск не подтверждает latency SLA SC-005.
- Совокупно проверены все 303 уникальных integration cases. Итоговые
  counters и команды сохранены в `media/phase5/verification.json`.
- `git diff --check` — PASS. Все восемь задач Phase 5 отмечены `[X]`;
  18 задач следующих фаз сохранены незавершёнными.

## Оставшаяся работа

Phase 6 / T044–T055: allowance endpoint, expiry, физический cleanup worker,
readiness/maintenance/telemetry и recovery rehearsal. Phase 7 / T056–T061:
итоговый aggregate, consumer transition, общая приёмка и rollout.
Текущая фаза создаёт cleanup jobs; физическое удаление в срок 24h и production
activation не объявляются выполненными. После коммита работа останавливается.
