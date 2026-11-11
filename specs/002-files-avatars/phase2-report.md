# Phase 2 — Foundational

Дата: 2026-11-11. Область: T005–T016 из [tasks.md](tasks.md).
Статус: все 12 задач завершены, обязательные проверки Phase 2 прошли.

## Реализация

- Core Media: восемь моделей, Unicode scalar validation, канонические MIME,
  лимиты назначений, fingerprint нормализованного payload с avatar version,
  deadline 24 часа, terminal snapshots/tombstones, leases и fencing.
- Аддитивная миграция `20261111122021_AddMedia`: восемь таблиц Media,
  отдельный schema marker, индексы, CHECK/UNIQUE и составные FK с NO ACTION.
  Generated discriminators требуют Ready/purpose/owner и Present/kind/file,
  а required slots — обязательные representations. Изменяемый state не
  является alternate key EF. Миграция сохраняет данные MVP и внешние обложки.
- Media write scope: READ COMMITTED, Accounts advisory lock → publication →
  allowance → avatar → files по UUID → uploads/objects/jobs. Lazy allowance,
  SQL SUM неистёкших резервов по DB clock, короткие claims, проверка fencing,
  сохранение object plan до передачи и retirement+cleanup в одной транзакции.
  Accounts role guard использует lock key RoleAdministrationStore, читает
  роли отдельным statement и не коммитит транзакцию вызывающего сценария.
- Spool: приватный постоянный том, эксклюзивное владение до reclaim, общий
  бюджет диска и transfer slots, streaming SHA-256/MD5, ограниченные размеры
  и deadline, очистка при ошибке/отмене/остановке, проверка writable/free space.
- Garage adapter: закрытые buckets, path-style SigV4, signed single-part PUT
  известной длины с Content-MD5 без chunk encoding, WHEN_REQUIRED checksums,
  bounded GET/Delete, проверка длины и SHA-256 до выдачи, безопасные ошибки.
- Skia codec: magic/container и полная декодировка JPEG/PNG/WebP, ограничения
  до bitmap allocation, отказ SVG/APNG/animated WebP и повреждённых PNG CRC,
  применение EXIF orientation, сохранение alpha и новый PNG без metadata.
- DI и binary transport: JSON остаётся 8 MiB, raw PUT обходит JSON buffering;
  purpose cap устанавливается после owner/state/role claim и до чтения тела.
  Kestrel 413 передаётся вызывающему upload-сценарию без подмены ошибки.
  Binary GET сначала верифицирует spool, затем повторяет resource authorization
  перед 200 или storage error. Range игнорируется; Content-Length,
  Content-Disposition, nosniff/no-store и 503 Retry-After заданы явно.

Фаза создаёт инфраструктуру; upload/library/avatar endpoints и workers ещё
не подключены. Прикладные сценарии и их HTTP acceptance относятся к следующим
фазам. T017 и последующие задачи не выполнялись.

## Настройки запуска

API теперь требует `Media:Storage:Endpoint`, runtime `AccessKey`/`SecretKey`
из защищённого provider и абсолютный `Media:Spool:Directory` вне webroot.
Production требует HTTPS; local/private HTTP разрешён явным opt-in только
в Development. Buckets должны быть разными и заранее provisioned.
Каждому API host выделяется свой приватный постоянный spool volume.
Пустые значения в appsettings являются безопасным шаблоном и не проходят startup.
Operator не получает Media grants; runtime API не получает DDL и не запускает
миграции. После отдельного применения AddMedia нужно повторно применить
`ops/database/permissions.sql` под provisioner.

Defaults: storage/readiness/transfer 60/3/120 секунд; spool 1 GiB;
4 shared transfer slots; processing 1 worker, batch 20, lease 180 секунд,
renewal 30 секунд; cleanup interval 5 минут и отдельный batch 100 для каждой
из трёх очередей, retry ceiling 15 минут. Product limits остаются в домене.

Параметры исходного профиля cleanup: 10 000 tombstones, 1 000 новых retirements
за 24 часа, 1 000 expired backlog и 1 000 новых expiries за 24 часа.
Номинальная ёмкость каждой независимой очереди — 28 800 записей/сутки
(`100 × 24h / 5min`). Обход 10 000 записей без притока — 500 минут;
warning по lag reconciliation и возрасту pending — 12 часов, раньше 24h deadline.
Это расчёт параметров, а не измеренная производительность. Workers, alerts,
проверка 12h scan/24h drain на реальных зависимостях и нагрузке — T045/T050–T053.

## Code review

Проведено самостоятельное ревью моделей, SQL миграции, grants, lock order,
SDK options, освобождения ресурсов, codec/container parsing и HTTP pipeline.
Устранены замечания:

1. Повторное освобождение spool через DI alias/host shutdown: Stop/Dispose
   идемпотентны; host ожидает отмены активных writers до освобождения тома.
2. Ошибки входного потока, включая Kestrel 413, не классифицируются как сбой
   диска; ошибки записи на диск обрабатываются отдельно.
3. Повторная авторизация выполняется также после storage/capacity/timeout
   failures; внешние коды 503 соответствуют `dependency_unavailable`.
4. PNG проверяет CRC каждого chunk; статический Skia FrameCount=0 допускается
   с независимым запретом animation chunks.
5. Retirement загружает и блокирует объект в своей DB session по ID, сохраняя
   атомарность cleanup; активные FK не позволяют retire используемый объект.
6. Для writable/free spool проверяется файловая система самого spool path,
   включая отдельный Linux mount.
7. Seed допускает только известные пустые таблицы Media и schema marker=1;
   неизвестные таблицы и Media user data по-прежнему запрещают seed.
   Старый View migration rehearsal использует именованную предыдущую миграцию.
   Отдельные тестовые API hosts получают разные spool volumes; Production
   fixtures используют HTTPS, сохраняя отрицательные проверки HTTP.

Блокирующих замечаний в пределах Phase 2 после исправлений не осталось.

## Constitution check

| Правило | Результат в пределах Phase 2 |
| --- | --- |
| I — модульность | Core без EF/HTTP/SDK; graph test фиксирует разрешённые Contracts edges, включая Media и role guard |
| II — C# | Nullable, async/cancellation, bounded resources; нет заглушек, блокирующего ожидания Task или подавления анализаторов |
| III — данные | Реальные negative constraints, empty/upgrade migrations, mutable state через EF, owner locks, 100-way quota и atomic rollback |
| IV — API/security | DTO сущностей не публикуются; private storage, purpose body policy, fresh authorization, безопасные headers/ProblemDetails и HTTPS gate |
| V — проверки | Unit без сети/БД; integration с реальными PostgreSQL/Garage и Linux codec; результаты ниже |
| VI — эксплуатация | Validated options, раздельные principals, process-only live, spool readiness/reclaim и bounded SDK calls |

Runtime gates последующих фаз не объявляются пройденными: полный Media
readiness/schema/sentinels, durable workers, rollout, recovery, consumer
transition и нагрузка четырёх transfers вместе с near-limit decode остаются
в соответствующих незавершённых задачах. Production activation не выполнялась.

## Проверки

Команды выполнялись из корня репозитория. Результаты текущего прогона
фиксируются в `artifacts/validation/media/phase2/`; SQL/EXPLAIN большого
эталонного набора — в `artifacts/validation/`.

- `dotnet restore BlogEngine.slnx --locked-mode` — PASS, pins/lock files сохранены.
- `dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror` — PASS,
  0 warnings/errors.
- `dotnet format BlogEngine.slnx --verify-no-changes --no-restore` — PASS.
- Unit suite — 85 passed, 0 failed/skipped.
- `FullDatasetQueryTests` отдельно после исправления seed — PASS; полный
  канонический набор, повторный seed/checksums и реальные SQL/EXPLAIN.
- Integration с `--filter 'Category!=BenchmarkDataset'` — 228 passed,
  0 failed/skipped. Вместе с отдельным BenchmarkDataset проверены все
  229 integration cases. Последний успешный результат —
  `phase2-integration-final.trx`; прежние неуспешные прогоны не являются итогом.
- Docker target `media-codec-validation` и `docker run --rm --memory 1g --cpus 2
  blog-media-codec-validation:local` — 5 passed, 0 failed/skipped в том же pinned
  ASP.NET Linux runtime, с непривилегированным UID. Это codec smoke, не T046 stress.
- `dotnet ef migrations script 20261024081130_AddViewDeduplication
  20261111122021_AddMedia --project src/BlogEngine.Infrastructure --startup-project src/BlogEngine.Infrastructure --configuration Release --no-build --output artifacts/validation/media/phase2/AddMedia.sql` — SQL получен и просмотрен; реальные empty/upgrade,
  grants и discriminator constraints проверены integration tests.
- `git diff --check` — PASS. `.specify/extensions.yml` отсутствует;
  before/after_implement hooks не зарегистрированы. Checklist markers сохранены.

Phase 2 завершена, работа остановлена. Продолжение —
отдельный запрос на Phase 3, начиная с T017.
