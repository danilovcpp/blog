# Phase 3 — US1: загрузка и закрытая библиотека

Дата: 2026-11-11. Объём: T017–T025, все отмечены `[X]`. Следующая фаза не запускалась.

## Реализация

| Задачи | Результат |
| --- | --- |
| T017 | Unit-правила назначения, размера, media type, Unicode scalars/White_Space/Cc, fingerprint, переходов и lease fencing |
| T018 | HTTP-контракты загрузки; реальные PostgreSQL/Garage, Kestrel и reference ingress; role/state matrix, controlled revoke races, retries и restart worker |
| T019 | Закрытая библиотека, роли и состояния, страницы и SQL/EXPLAIN; имена/хеши 100 вложений, включая 1 байт и 50 MiB, до/после restart API/Garage и 24h |
| T020 | Нормализованный owner-scoped init/replay, резерв под owner lock, DB clock, status/effective expiry и идемпотентная отмена |
| T021 | Durable object plan до S3, уникальные attempt keys, fenced PUT, точный replay, terminal mismatch/oversize, unknown outcome и прерванный body |
| T022 | Persistent Processing, lease renewal в отдельной DB session, bounded backoff, проверка исходника и атомарный reserve→used/Ready/result; регистрация purpose processors |
| T023 | Bounded library projection и bigint offset, metadata/private original, свежие локальные роли с ресурсом при повторной авторизации |
| T024–T025 | Восемь маршрутов, отдельные DTO, Location, явные nullable result/errorCode, 201/204/202/200, binary headers и no-store |

В этой фазе зарегистрирован только processor `Attachment`. PublicationImage и
Avatar подключаются отдельными обработчиками в T029/T040. Клиентские handlers
не принимают ownerId для записи. Upload остаётся приватным владельцу даже для
Admin; административное чтение относится только к готовым обычным файлам.

Короткие SQL-транзакции используют порядок Accounts role guard → allowance →
file → upload. Сеть и spool находятся вне транзакций. Ready и immutable result
сохраняются вместе с одной дельтой used. Init/Completed replays выполняются до
проверок текущих роли/expiry; PUT и новые переходы проверяют актуальные роли.
Reject/cancel/expiry создают retirement и durable cleanup jobs, но физический
cleanup worker остаётся задачей T050.

Для сохранения работоспособности прежнего `/openapi/v1.json` встроен отдельный
контрактный срез `contracts/openapi-us1.yaml`: только восемь реализованных
операций и шесть новых schemas, включая отдельный MediaProblem с новыми кодами.
Схемы и 35 операций MVP сохранены. Полный
aggregate, обновление consumers и независимая проверка всех будущих Media
операций остаются T056–T058; эти задачи не отмечены выполненными.

## Code review

Проверены все новые production-файлы и изменения общего write store, DI,
binary middleware/маршрутов, нормативного OpenAPI и тестовой инфраструктуры.
Исправлены найденные замечания:

1. Поколение PUT lease сохраняется перед повторным lock/reload в object-plan
   contract: иначе первый PUT ошибочно получал concurrency conflict.
2. Чтение актуальной роли отделено от немедленного отказа общего write scope.
   Это позволяет историческому replay вернуть результат, а revoked complete
   атомарно сохранить Rejected/forbidden и освободить резерв до ответа 403.
3. Upload переводится в Completed и flush выполняется до расчёта available:
   его прежний резерв не считается второй раз при переводе в used.
4. Неопределённый S3 outcome сохраняет tracked объект и cleanup job; повтор
   получает новый key. Retirement учитывает время transfer lease.
5. Активный PUT replay блокирует начало Processing до release. Lease renewal
   использует отдельный scope/DbContext, не конкурирует на одном контексте.
6. Окончательная авторизация закрытого download читает текущий Admin grant
   вместе с файлом и выполняется также после missing/corrupt GET. Потеря
   доступа возвращает 404 до storage 503 и до binary headers.
7. Purpose принимает только точное строковое значение контракта. Число,
   строковый номер enum и неверный регистр отвергаются. Недоступный display
   для Attachment даёт 404. Ошибки Media сохраняют no-store после Clear.
8. Прежний OpenAPI gate расширен US1-срезом: новые маршруты не ломают
   публикацию контракта и не скрываются за старым overlay.
9. Тяжёлые library/transport tests выделены в непараллельную коллекцию
   `Media transfers`. Это отделяет передачу/хеширование сотен MiB и restart
   Garage от 100-way DB/auth сценариев других коллекций. Внутренняя
   конкуренция сценариев и лимиты production API не изменены.

Блокирующих замечаний в пределах Phase 3 после исправлений не осталось.

## Constitution check

| Принцип | Подтверждение в пределах Phase 3 |
| --- | --- |
| I — модульность | Application/Domain без EF, HTTP и SDK; Accounts через Contracts; architecture tests |
| II — C# | Nullable, async/cancellation, явные side effects; без заглушек и подавления анализаторов; build/format |
| III — данные | Реальный PostgreSQL; role/owner/file/upload locks, fencing, атомарный quota/result; bounded projection и SQL/EXPLAIN |
| IV — API/security | Verified actor, свежие роли, скрывающие 404, DTO, строгий input, private storage, ProblemDetails и binary integrity до headers |
| V — тесты | RED: новый HTTP init возвращал 404; далее реальные HTTP/DB/storage, барьеры и управляемые часы, негативные сценарии и регрессия |
| VI — эксплуатация | Durable Processing/retry/renewal, restart recovery, bounded transfers, safe structured worker errors, cancellation при остановке |

Изменений схемы и grants нет. Добавлена уже закреплённая зависимость
`Microsoft.Extensions.Hosting` 10.0.13 для Infrastructure worker; SDK и прежние
package pins не менялись, обновлены затронутые lock files.

`.gitignore`/`.dockerignore` проверены; дополнительных publishing, ESLint,
Prettier, Terraform или Helm ignore-файлов для этого backend изменения не нужно.
`.specify/extensions.yml` отсутствует: before/after_implement hooks не зарегистрированы.
Оба checklist файла остались read-only: backend 36/36, requirements 16/16.

## Проверки

Evidence находится в `artifacts/validation/media/phase3/` (игнорируется Git).

- Locked restore всего `BlogEngine.slnx` — PASS.
- Release build с `--no-restore --disable-build-servers -m:1 -warnaserror` —
  PASS, 0 warnings/errors.
- `dotnet format BlogEngine.slnx --verify-no-changes --no-restore` — PASS.
- Unit suite — 91 passed, 0 failed/skipped; `phase3-unit-final.trx`.
- Полный последовательный integration suite, включая BenchmarkDataset —
  258 passed, 0 failed/errors/skipped/not-run/warnings, 744.638s;
  `phase3-integration-serial.trx`. Запуск того же test assembly через его
  xUnit in-process runner с `-parallel none -maxThreads 1 -failSkips -failWarns`.
- Финальный обычный integration suite с четырьмя параллельными collections
  и изолированной `Media transfers` — 257 passed, 0 failed/skipped/warnings,
  3m03s; `phase3-integration-final-parallel.trx`. Команда:
  `dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build --no-restore --filter 'Category!=BenchmarkDataset'`.
  Вместе с предыдущим полным прогоном проверены все 258 integration cases.
- `hundred-attachments.json` — длины/SHA-256 всех 100 исходников;
  `library-query.json` — SQL, один data query и реальный EXPLAIN ANALYZE/BUFFERS.
- Kestrel проверен с Content-Length и chunked: ровно 50 MiB, mismatch→400,
  purpose+1→413, Rejected и сохранение принятых байтов при replay. Reference
  proxy с ceiling 51 MiB различает API 413 и отказ до API с прежней попыткой.
- Revoke-before-complete/worker и Ready-before-revoke проверены независимыми
  DB sessions с барьерами. Первое даёт Rejected/forbidden без used/reserve,
  второе — Ready и исторический replay после отзыва.
- `git diff --check` — PASS.

Промежуточные прогоны не являются итоговыми: reference proxy терял ранний 413
при отправке тела без Expect/Continue; в общем параллельном прогоне также был
503 старого 100-way LikeConcurrency. После исправления proxy и изоляции тяжёлой
transfer collection финальный штатный параллельный набор проходит полностью.
Проверки конкурентных сценариев не сокращены. Прежние resolved package versions
сравнены с HEAD во всех четырёх изменённых lock files — без изменений;
evidence: `package-pins.json`.

## Оставшаяся работа

Phase 3 не включает T026 и далее: связи публикаций и обработку изображений,
аватары, DELETE готового файла, allowance endpoint, физическую cleanup/expiry
очередь, полную Media readiness/maintenance, recovery/rollout и consumer
transition. Их гарантии не объявляются пройденными. Production activation,
stress 1 GiB с четырьмя передачами и image decode и восстановление recovery
set в этой фазе не выполнялись.

Работа останавливается после Phase 3 и коммита её изменений.
