# Implementation report: файлы и аватары

## Phase 1 — Setup (2026-11-11)

Область работы — только T001–T004. Phase 2 и последующие фазы не выполнялись.
Статус: Phase 1 завершена; T001–T004 отмечены `[X]`.
Checklists: requirements 16/16, backend 36/36; markers не изменялись.

### Изменения

- T001: централизованные pins AWSSDK.S3 4.0.104.12, AWSSDK.Core 4.0.103,
  SkiaSharp/Linux native assets 4.155.0; обновлены шесть затронутых lock files.
  Существующие resolved versions, SDK/EF/Npgsql/PostgreSQL pins сохранены.
  Пересчёт Performance lock также убрал зависимости, предоставляемые его
  существующим Microsoft.AspNetCore.App framework reference.
- T002: Garage v2.5.0 закреплён одним multi-platform manifest digest
  `sha256:677e5b758e4425a325b811c425c026bf35a4bf42f7ddbaa773de3f4fd779a5a5`
  в Compose, fixture и production inventory. Digest проверен командами
  `docker buildx imagetools inspect dxflrs/garage:v2.5.0` и pull exact reference.
  Dev profile `media`: replication=1, consistent, отдельные постоянные volumes,
  S3/Admin на loopback, RPC не опубликован. Production template: replication=3,
  consistent, три независимых домена, приватные RPC и отдельные volumes/capacity.
- T003: реальный повторяемый PowerShell provisioner создаёт private buckets,
  сохраняет runtime credentials через protected-provider callbacks, отделяет
  Admin token, снимает owner/create-bucket и выдаёт read/write только Media.
  Неожиданные чужие grants отклоняются. Sentinel: 29 bytes, известный SHA-256,
  SigV4/Content-MD5. Вывод содержит только non-secret descriptor.
- T004: одноразовая Garage fixture с реальными persistent volumes, защищёнными
  случайными credentials, запуском настоящего provisioner через stdin provider,
  управляемым stop/restart и подключением к двум ApiFactory. PostgreSQL/JWKS,
  clocks и существующие concurrency helpers переиспользуются. Четыре smoke-теста
  проверяют приватность, ограничения grants, идемпотентность provisioning,
  целостность sentinel/bytes и сохранность после отказа.

Инструкции: [storage setup](../../ops/media/README.md).

### Code review и constitution

Самостоятельное ревью полного diff выполнено. Блокирующих замечаний не осталось.
Проверены безопасность callbacks/вывода, минимальные privileges, таймауты,
изолированность контейнеров/volumes, очистка ресурсов и обратная совместимость
необязательного подключения Garage к существующим fixtures. Ревью добавило
отказ при лишних grants и проверку восстановления завышенных privileges.
При проверке исправлены file/directory resource mapping Testcontainers,
обновление ephemeral ports после restart и точный SDK exception для missing key.

| Принцип | Проверка в пределах Setup |
|---|---|
| I: модульность | SDK-зависимости только Infrastructure; Core не зависит от S3/Skia; новых production-модулей/HTTP paths нет |
| II: завершённый C# | async/cancellation в fixture, ограниченный child process, disposal, build/analyzers/format без подавлений |
| III: целостность данных | схема/миграции не меняются; private object bytes сохраняются после restart; existing migration tests входят в regression suite |
| IV: безопасность | protected providers, no secret CLI/output, private buckets, forbidden anonymous/foreign-bucket access, отсутствие owner/create-bucket |
| V: тесты | RED: новый smoke не собирался без GarageFixture; затем реальные PostgreSQL/Garage, без замен БД/storage и без живого Identity |
| VI: эксплуатация | exact pins, постоянные volumes, bounded operations, cleanup контейнеров/процессов, production topology описана отдельным gate |

`.gitignore` и `.dockerignore` проверены; `.gitignore` дополнен `.env*`.
Новых package-publishing, ESLint/Prettier, Terraform или Helm файлов в этой фазе нет.
`.specify/extensions.yml` отсутствует: before/after hooks не зарегистрированы.

### Проверки

| Команда / проверка | Результат |
|---|---|
| `dotnet tool restore` | PASS, dotnet-ef 10.0.13 |
| `dotnet restore BlogEngine.slnx --locked-mode` | PASS |
| `dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror --disable-build-servers -m:1` | PASS, 0 warnings/errors |
| `dotnet format BlogEngine.slnx --verify-no-changes --no-restore` | PASS |
| `dotnet test tests/BlogEngine.UnitTests/BlogEngine.UnitTests.csproj -c Release --no-build` | PASS, 82/82, 0 skipped |
| `dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build` | PASS, 205/205, 0 skipped, 6m27s |
| Финальный `--filter FullyQualifiedName~MediaFixtureTests` после добавления concurrent `/me` через общий barrier | PASS, 4/4, 0 skipped |
| PowerShell AST parser обоих scripts | PASS |
| `docker compose --profile media config --no-interpolate --format json` | PASS; digest/profile, loopback ports и два volumes проверены |
| TOML parsing, replication 1/3, consistent, раздельные paths | PASS |
| Совпадение трёх manifest pins; сравнение прежних resolved NuGet versions | PASS |
| `git diff --check` | PASS |

TRX: `artifacts/test-results/phase1/unit.trx`, `integration.trx` и
`media-setup-final.trx` (игнорируются Git). После последнего изменения только
smoke-теста повторены build, format verification и затронутые четыре теста.
Блокировок завершения Phase 1 не осталось.

### Ограничения и оставшаяся работа

Production не развёрнут: inventory содержит явные placeholders. Трёхузловая HA,
restore, native codec в Linux API image, Media schema/readiness и пользовательские
HTTP-сценарии относятся к следующим фазам и здесь не объявлены проверенными.
Внешний production vault не подключался; callbacks проверены защищённым
in-memory test provider. После restart Docker может сменить временные порты
fixture: клиенты/API factories создаются заново из её обновлённой конфигурации.
