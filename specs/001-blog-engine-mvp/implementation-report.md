# Implementation report: Blog Engine MVP

Дата: 2026-10-24. Constitution: 1.0.0. Scope этого изменения: Phase 10.

**Статус: Phase 10 завершена в согласованном объёме; T114–T125 закрыты.**
Решением владельца от 2026-10-24 нагрузочный SC-005 не требуется на текущем
этапе проекта и для релиза 1.0.0. T121 закрыт по изменению объёма без прогона;
успешная нагрузочная приёмка и показатели p95/p99 не заявляются.
Следующая фаза не начиналась; публикация версии 1.0.0 этим отчётом не подтверждается.

Изменение касается требований и документации. Исходные результаты проверок
сохранены; полный нагрузочный прогон не выполнялся. После предоставления
владельцем образа Identity отдельно выполнен реальный интеграционный smoke
(результат ниже); production-код не менялся.

## Реализовано в Phase 10

| Задачи | Результат |
|---|---|
| T114, T117 | Consumer walkthrough всех 35 операций US1–7; сравнение OpenAPI 3.1, required/nullable/enum, responses, parameters/version и security. Нормативный YAML встроен в API; transformer проверяет method/path/operationId перед публикацией. Удалены четыре дублирующих transformer. Health возвращает контрактные live/ready; cache headers действуют и для ошибок. В YAML уточнены существующие правила Visible/Hidden/Deleted через oneOf. |
| T115 | Проверка пустой схемы и каждого из пяти реальных migration baselines с данными, повтор idempotent SQL, constraints/indexes/grants и отсутствие model drift. Добавлен исполняемый PowerShell wrapper. Новых production migrations нет. |
| T116 | Production runtime без DDL/startup migrations, command timeout и отмена запроса без internal error. Полный suite также проверяет PG/JWKS outage/recovery, CORS, startup options, safe structured errors и остановку cleanup. |
| T118 | Полный sc005-v1 / 20261024: 100 000 Published, 1 000 000 comments, 1 000 000 unique likes, aggregate 10 000 000 views, 10 000 users/1 000 authors, 200 categories/2 000 tags, 200 000/500 000 joins и нормативные частоты/размеры. Данные и повторная независимая генерация совпадают по canonical checksums всех таблиц. Seed проверяет одноразовую БД, migration/model/PG profile и прежний manifest; неизвестные данные не удаляет. |
| T119 | Версионированный 400-slot план, каталог селективностей/ожидаемых результатов и checksums; ephemeral RSA/JWKS/JWT в памяти, 100 VU, 120 s warmup + 600 s measurement, request timeout 5 s и drain ≤30 s. Все попытки входят в nearest-rank percentiles/failures; общий и восемь feed variants проверяются отдельно. Ошибочный payload, недренированный блок, неверный стенд, недостающие SQL/метрики или изменённый dataset запрещают успех. Диагностический режим всегда отклоняет SC-005 acceptance. |
| T120 | 736 HTTP/persistence SQL/EXPLAIN сценариев на полном seed, включая все 400 профильных запросов, обе сортировки/размеры/страницы, категории/теги/combined и max filters, workspace/moderation/roots/replies. Справочники проверены на 200/2 000 и отдельной полной копии с 2 000/20 000: default/20/100, first/middle/last/beyond, ties, один data SELECT, ≤pageSize+1 reader rows, четыре поля DTO, отсутствие tracking. Индекс (name,id) и его C collation проверены в PostgreSQL. Сохранены 787 файлов evidence; на двухстраничном списке middle совпадает с first. Новых индексов не потребовалось. |
| T121 | Закрыт решением владельца от 2026-10-24: нагрузочный SC-005 исключён из обязательной приёмки текущего этапа/релиза 1.0.0. Прогон не выполнялся; benchmark, профиль и его критерии сохранены. |
| T122 | Отдельные non-root Linux x64 Dockerfile API/Operator с проверенными amd64 digests SDK/runtime; deployment guide с credentials/key ring, reviewed SQL, backup/lock/rollout/recovery и ingress/health. Образы собраны и запущены. |
| T123–T125 | Quickstart, README/database/deployment docs, итоговое ревью, матрица требований и этот отчёт. Доказательства и исходы команд сохранены в игнорируемом artifacts/. |

## Проверки и evidence

Проверялось рабочее дерево поверх `3762f7fac2a3a904841b79c34e14696a00e6987d`.
Manifest явно фиксирует dirty tree и source SHA-256
`26c1de9253a1330ef41e23d9029a69cf93749656479adb80e4e1f5d15747c8d6`.
Этот checksum охватывает C# sources/projects/lock files, SDK/package props и YAML;
отчётные Markdown и validation wrappers в него не входят.
Schema SQL SHA-256: `dbabfdb7d18d6ea92528269dad4c46eb234889f47ffa7341d7d3ace0f831dd02`.

| Команда / проверка | Фактический результат |
|---|---|
| dotnet tool restore; dotnet restore BlogEngine.slnx --locked-mode | PASS; SDK 10.0.402, dotnet-ef 10.0.13 |
| dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror | PASS; 0 warnings/errors; также полный Linux build в pinned SDK image |
| dotnet format BlogEngine.slnx --verify-no-changes --no-restore; git diff --check | PASS |
| Полный unit suite | 82/82 Windows и 82/82 Linux; без skipped |
| Полный integration suite на PostgreSQL 18.6 | 201/201 в финальных непересекающихся наборах: 198 regression +3 query-inspection; без skipped |
| Linux contracts/operations/preparation/protocol subset | 13/13 на реальном PostgreSQL и JwtBearer; без skipped |
| pwsh -File scripts/validation/Invoke-MigrationValidation.ps1 -NoBuild | PASS; 10/10 tests, EF model drift отсутствует, reviewed idempotent SQL создан |
| pwsh -File scripts/validation/Invoke-QueryInspection.ps1 -NoBuild | PASS; 3/3 tests, полный dataset/повторная генерация/SQL/plans/taxonomy copy |
| Container smoke | PASS; API UID 1654, live=200/live, DB outage ready=503; Operator без config=5, с config без аргументов=2 |
| Реальный Identity/Blog smoke | PASS; 25/25 проверок через `scripts/validation/Invoke-IdentityIntegrationSmoke.ps1`; реальные `danilovcpp/identity:latest` / `danilovcpp/blog:phase10`, одноразовые PG17/18.6 и Mailpit; cleanup PASS |
| 100-VU diagnostic | 1200 запросов в полных блоках, 0 failures, response contract PASS; acceptance=false |
| Полный SC-005 | **ОТЛОЖЕН решением владельца от 2026-10-24; не требуется для текущего этапа/релиза 1.0.0. Прогон не выполнялся.** |

Финальные результаты: [unit TRX](../../artifacts/test-results/phase10/unit-final.trx),
[198 regression TRX](../../artifacts/test-results/phase10/integration-regression-final.trx),
[query TRX](../../artifacts/validation/queries/query-inspection.trx),
[migration TRX/SQL](../../artifacts/validation/migrations/),
[Linux TRX](../../artifacts/validation/phase10-linux/),
[contract drift](../../artifacts/validation/contracts.json),
[consumer walkthrough](../../artifacts/validation/consumer-walkthrough.json),
[query summary/hashes](../../artifacts/validation/queries/sc005-summary.json),
[taxonomy evidence](../../artifacts/validation/queries/taxonomy/),
[оценка фактических taxonomy plans](../../artifacts/validation/queries/taxonomy/assessment.md),
[seed manifest/catalog/diagnostic](../../artifacts/performance/sc-005/preparation/),
[container smoke](../../artifacts/validation/container-smoke.json),
[real Identity smoke](../../artifacts/validation/live-identity/result.json),
[полный отчёт](../../artifacts/validation/final-report.md).
Предварительные red/regression TRX сохранены для диагностики; они заменены указанными
финальными проверками после исправления замечаний.

На baseline максимальное измеренное SQL execution time справочников составило
0.087 ms для categories и 0.742 ms для tags; на expanded copy — 0.282/3.167 ms.
Seq Scan выбран в 9/11 и 6/12 baseline-сценариях; expanded copy использовала
совместимые name/id indexes во всех проверенных сценариях. На малых таблицах
и больших долях прочитанного набора Seq Scan/sort согласуется с измеренной
стоимостью; порядок, лимиты, проекция и C collation подтверждены независимо
от planner node. Эти измерения не являются HTTP latency-приёмкой SC-005.

## Code review и constitution

Проведено самостоятельное code review итогового diff. Исправлены дублирование
OpenAPI-правил, несовпадение health payload, неполная документация cache headers,
body-read timeout и условия принятия benchmark, граничное заполнение Markdown,
guard counts в query tests и аргумент configuration EF wrapper. Подавления
анализаторов, отключения тестов, production stubs и расширения за FR-042 нет.

| Принцип | Итог проверки |
|---|---|
| I. Модульность | PASS: Core и владение данными не изменены; архитектурные тесты проходят. Нет новых service boundaries/frameworks. |
| II. Завершённый C# | PASS: async cancellation, NRT, build/format/analyzers; нет рабочих TODO/NotImplementedException. |
| III. Данные | PASS: реальный PG18.6, вся migration chain, ограничения/права, bounded projections/SQL/plans, независимая expanded copy. |
| IV. Контракты/безопасность | PASS: единый OpenAPI, строгие DTO, роли/versions/ProblemDetails, no-store; закрытый текст/секреты не раскрываются. |
| V. Проверяемость | Обязательные автоматические gates PASS. SC-005 исключён из текущей приёмки решением владельца; harness и diagnostic не подтверждают его прохождение. |
| VI. Эксплуатация | PASS для автоматических fixtures/контейнеров: health/config/timeout/cancellation/shutdown, separate principals, non-root hosts, документированный deployment. |

Блокирующих замечаний к проверенному коду не осталось. T121 закрыт по изменению
объёма; SC-005 не блокирует текущий этап/релиз 1.0.0.

## Покрытие требований

| Требования | Проверенные сценарии | Итог |
|---|---|---|
| FR-001–011 | Publishing/Taxonomy unit, lifecycle, constraints, races, migration и consumer | PASS |
| FR-012–017 | Reading filters/ties/pages/detail/counters, bounded queries и full seed | PASS |
| FR-018–025 | Discussions/Moderation states/ownership/tree/ancestors/races/placeholders | PASS |
| FR-026–031 | Likes/Views idempotency, multi-host concurrency, exact window, rollback/cleanup | PASS |
| FR-032–038 | Реальный JWT/JWKS pipeline, local roles/bootstrap, Operator/audit/DB privileges; дополнительный smoke реальных Identity/Blog images | PASS в fixtures и одноразовом Development-окружении |
| FR-039–040 | 35-operation consumer, OpenAPI/ProblemDetails/strict input/trace/cache | PASS |
| FR-041–042 | Конфигурация одной установки, module graph, отсутствие дополнительных product features | PASS |
| SC-001–004, SC-006–008 | Полные соответствующие story suites и итоговый consumer | PASS |
| QR-001 | Data/query gates, bounded SQL и проекции на полном seed пройдены | PASS |
| SC-005 | 10-minute latency/failure acceptance на R-06 не выполнялась; требование снято для текущего этапа/релиза 1.0.0 | **ОТЛОЖЕН по решению владельца; T121 закрыт** |
| QR-002–004 | Unit/real-PG HTTP/persistence/Operator, architecture/build/analyzers и runtime operations | PASS применимых автоматических gates |

## Ограничения и возможные следующие проверки

T121 закрыт без нагрузочного прогона по решению владельца от 2026-10-24.
SC-005 не требуется на текущем этапе проекта и для релиза 1.0.0. Профиль R-06,
seed, harness и все исходные thresholds сохранены для возможного будущего запуска;
достижение p95/p99 и failure thresholds не подтверждено.

Тестовый `JwksFixture` по-прежнему служит для детерминированных unit/integration
сценариев. Дополнительно 2026-10-24 выполнен smoke с реальным
`danilovcpp/identity:latest`, digest
`sha256:2d2019371e155137fd084c1f21e7877a0c78fa4b68a1d37f10a98ac74bc77580`,
image ID `sha256:6f2d587befc5bded369a7ca71a637bdd5974a4f23182a6a5d47bba2f0884571f`.
Blog image ID совпадает с опубликованным `danilovcpp/blog:phase10`:
`sha256:f67ed3a60e890ebb3bcb37d8779ea4339c51b25b34c8c82a95d12cd4ba7ca170`.

Команда: `pwsh -NoProfile -File scripts/validation/Invoke-IdentityIntegrationSmoke.ps1 -IdentityMigrationSql D:/Space/Personal/identity-service/migration.sql`.
Все 25 проверок прошли: регистрация/SMTP/verification/login/profile, публичный JWKS
и приём реального JWT Blog API, стабильный issuer/sub mapping и Reader bootstrap,
запрет записи Reader, выдача Author через настоящий Operator и видимость с прежним
JWT, создание/публикация, изоляция второго пользователя и foreign draft 404,
wrong audience/issuer/signature 401, refresh/rotation, replay/family revocation,
logout и отсутствие сгенерированных паролей/полученных токенов в контейнерных логах.
После logout Identity profile возвращает 401, Blog продолжает принимать JWT
до exp + clock skew согласно контракту локальной проверки без introspection.

Smoke использует Development и loopback HTTP JWKS в общем network namespace,
отдельные PostgreSQL 17/18.6 и Mailpit. Рабочие данные/настройки не использовались;
одноразовые контейнеры, сеть и secret-файлы удалены (cleanup PASS). Проверка
production HTTPS/ingress и production rollout не выполнялись и не заявляются.
Phase 10/MVP завершены в согласованном объёме; релизные Git/Docker-теги 1.0.0
этим изменением не создавались.
