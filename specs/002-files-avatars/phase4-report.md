# Phase 4 / US2 — файлы в публикациях

Дата: 2026-11-11. Область: T026–T035. Следующие фазы не выполнялись.

## Реализация

- PublicationImage использует общие init/PUT/complete и persistent processing.
  Проверенный исходник сохраняется без изменения. Display — новый PNG после
  проверки контейнера, полной декодировки и EXIF orientation, с alpha,
  пропорциями и длинной стороной до 2560, без увеличения. Объект Display
  получает durable plan до S3 PUT. Present и representation записываются
  до Ready, вместе с результатом и переводом reserve → used в одной транзакции.
  Невалидное изображение получает Rejected; устаревшие планы получают cleanup jobs.
- Новые поля записи различают пропуск, null и пустой массив. Пропуск
  сохраняет managed links, `coverFileId:null` и пустые массивы снимают связи.
  Внешняя HTTPS-обложка и managed cover взаимно исключаются. Точное эхо
  текущего managed URL допустимо только на соответствующей статье при
  пропущенном `coverFileId`. Старое поведение очистки внешнего coverUrl сохранено.
- Media проверяет лимиты 1/50/20, повторы внутри роли, Ready, назначение и
  владельца публикации, включая команды Admin. Cover и Inline могут ссылаться
  на одно изображение с независимыми позициями. Связи, текст, classification
  и версия изменяются атомарно. Delete статьи снимает связи и сохраняет библиотеку.
- Все команды публикации берут Accounts advisory guard перед publication lock,
  читают свежие локальные роли и держат guard до commit. Результат команды
  строится до commit и больше не дочитывается HTTP-обработчиком после него.
- Feed/workspace cards содержат effective coverUrl и coverFileId. Detail
  содержит разрешённые inlineImages/attachments, workspace detail — также ID
  для редактирования. Чтения используют короткий read-only REPEATABLE READ.
  Карточки не загружают массивы Media; detail использует один общий batch.
- Добавлен publication-scoped content с attachment/display и закрытый display.
  Доступность статьи, точная связь, Ready и актуальная роль Admin проверяются
  одним SQL snapshot до чтения объекта и повторно перед success или storage 503.
  Потеря доступа даёт скрывающий 404, включая отказ Garage GET. Оригиналы
  изображений доступны только через закрытую библиотеку owner/Admin.
  Выдача использует verified spool, no-store/nosniff, корректный Content-Length
  и Content-Disposition; Accept проверяется для фактического варианта.
- DELETE обычного файла проверяет назначение до usage/tombstone. Avatar ID
  всегда даёт 404. Used, логическое удаление, снятие object refs/representations
  и retirement с cleanup jobs изменяются одним commit. Связанный файл даёт
  file_in_use; повтор owner/Admin не списывает квоту второй раз.
- Промежуточный `contracts/openapi-us2.yaml` публикует только реализованные
  US1/US2. Historical baseline и исходная feature delta сохранены. Полная
  агрегация, consumer transition и production activation остаются Phase 7.

## Code review

Самостоятельно проверены изменённые application, persistence, read models,
HTTP/binding, регистрация DI, контракт и тесты. Исправлены замечания:

1. Убрано чтение ответа команды после commit. Управляемый тест допускает
   второй commit до отправки первого HTTP-ответа и проверяет первый snapshot.
2. Убрана лишняя загрузка полных Media arrays для workspace cards; cover
   проецируется в основном SQL, число запросов списка остаётся постоянным.
3. Лимиты/уникальность ID сведены в одно domain-правило, применяемое до
   чтения и блокировки набора файлов.
4. Presence flags сделаны приватными, чтобы не расширять допустимые поля JSON.
5. Binary Accept проверяется для выбранного PNG/octet-stream варианта.
6. Исправлены ссылки и самостоятельность промежуточного OpenAPI slice;
   обновлены строгие ожидания существующих consumer tests.
7. Сборщик evidence исключает transaction control из data queries/EXPLAIN.
   Барьеры учитывают обёртки SQL EF и блокируют нужный этап сценария.

Открытых замечаний, препятствующих завершению US2, по ревью нет.

## Constitution 1.0.0

| Принцип | Подтверждение в пределах Phase 4 |
| --- | --- |
| I — модульность | Core без EF/HTTP/Skia; Publishing → Media.Contracts и Accounts.Contracts; architecture tests |
| II — C# | Nullable, async/cancellation, явные границы, нет заглушек и подавления анализаторов; build/format |
| III — данные | Role → publication → allowance → files; короткие транзакции без S3; version/usage rollback, FK, fenced image finalization; bounded SQL и EXPLAIN |
| IV — API/security | Presence-aware DTO, ресурсные 404, свежие роли при выдаче и записи, private originals, строгие варианты/Accept, ProblemDetails и no-store |
| V — тесты | RED до реализации: три новых HTTP-теста падали; затем unit, настоящий HTTP/PostgreSQL/Garage, controlled races/snapshots, restart/hash parity и регрессия |
| VI — эксплуатация | Durable plans/retry/retirement, bounded spool и decode, cancellation, закрытый storage, отсутствие новых секретов и небезопасного logging |

Изменений схемы, grants, package pins и lock files нет. Дополнительные
миграции не нужны; существующие проверки constraints/migrations включены
в регрессионный набор. `.gitignore`/`.dockerignore` проверены. Новых инструментов,
требующих отдельных ignore-файлов, нет. Прототипы `mockups/` не менялись.
Checklist markers сохранены: backend 36/36, requirements 16/16.
`.specify/extensions.yml` отсутствует; before/after_implement hooks не зарегистрированы.

## Проверки

Команды выполняются из корня репозитория; TRX находятся в
`artifacts/test-results/`, SQL/EXPLAIN и hashes — в
`artifacts/validation/media/phase4/`. Оба каталога игнорируются Git.

- `dotnet restore BlogEngine.slnx --locked-mode` — PASS.
- `dotnet build BlogEngine.slnx -c Release --no-restore --disable-build-servers -m:1 -warnaserror`
  — PASS, 0 warnings/errors.
- `dotnet format BlogEngine.slnx --verify-no-changes --no-restore` — PASS.
- Unit suite — 95 passed, 0 failed/skipped; `phase4-unit-verified.trx`.
- Integration regression без BenchmarkDataset — 278 passed, 0 failed/skipped;
  `phase4-integration-verified.trx`.
- Финальный набор US2 с дополнительной матрицей изображений и delete/Avatar
  tombstones — 21 passed, 0 failed/skipped; `phase4-us2-final.trx`.
  Он также повторно проверяет rollback classification и точный binary Accept.
- Отдельный BenchmarkDataset — 1 passed, 0 failed/skipped;
  `phase4-benchmark-verified.trx`, 5m13s. Проверены повторный canonical seed,
  400 основных HTTP-сценариев, фильтры/страницы, реальные SQL/EXPLAIN,
  diagnostic response contract и clone с расширенными taxonomy.
  Совокупно проверены все 281 уникальный integration case; latency SLA
  локальным диагностическим запуском не объявляется подтверждённым.
- Docker target `media-codec-validation`, затем
  `docker run --rm --memory 1g --cpus 2 blog-media-codec-validation:phase4`
  — 7 passed, 0 errors/failed/skipped/not-run в закреплённом ASP.NET Linux runtime.
- `detail-query.json`: 4 data queries для detail, общий Media batch,
  настоящий PostgreSQL EXPLAIN ANALYZE/BUFFERS. Списки остаются в прежнем
  постоянном бюджете; полные массивы доступны только detail.
- `private-sources.json`: SHA-256 десяти закрытых исходников; имена и hashes
  проверены до/после перезапуска API и Garage.
- Проверены 100-way version race с одним успехом и 99 conflicts, оба порядка
  attach/delete и revoke/publication commit, согласованность snapshots,
  успешный/неуспешный storage GET с отзывом доступа, PNG fit/alpha, legacy writes.
- `git diff --check` — PASS.

Сводные counters и перечень прогонов сохранены в `verification.json`.

Промежуточные ошибки не являются успешными проверками: исходный OpenAPI slice
имел неверные ссылки; прежние тесты не учитывали новые поля и transaction
control; isolated renderer test наследовал spool 1 MiB. Исправлены контракт,
ожидания, SQL capture и бюджет изолированной фикстуры. Один запуск сборки
пересёкся с тестовым процессом и получил Windows file locks; повторная сборка
после завершения процесса прошла без предупреждений.
В первом большом прогоне строгий benchmark consumer также отверг новые поля
публикации; он обновлён с сохранением точной проверки разрешённых полей и
ожидаемых пустых Media-значений canonical seed. Повторный прогон прошёл полностью.

## Оставшаяся работа

Следующие фазы: собственные аватары (T036–T043), квота/expiry/физическая
очистка и эксплуатационная готовность (T044–T055), итоговые контракты,
consumer transition и rollout (T056–T061). Эти гарантии не объявляются
выполненными. В этой фазе создаются cleanup jobs; их физическое выполнение
остаётся US4. Production activation и stress четырёх transfers с near-limit
decode не выполнялись. После коммита работа останавливается на Phase 4.
