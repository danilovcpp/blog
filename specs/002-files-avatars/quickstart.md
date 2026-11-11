# Quickstart: validation файлов и аватаров

Это сценарии приёмки **после implement**. Новые endpoints, Media fixtures
и validation scripts пока не реализованы. Текущая команда plan проверяет
документы; она не свидетельствует об успешных runtime-тестах.
Нормативные детали: [HTTP](contracts/http-api.md),
[OpenAPI delta](contracts/openapi.yaml), [data model](data-model.md),
[operations](contracts/operations.md).

## Prerequisites

- PowerShell7, SDK10.0.402, Linux Docker Engine, доступ к NuGet и закреплённым
  images. PostgreSQL18.6 с production-compatible settings, Garage2.5.0 с exact
  manifest digest из реализации. Запуск только из корня repository.
- Unit tests без сети/БД. Integration fixtures создают изолированные БД,
  private buckets, одноразовые JWT/JWKS и keys; живой Identity не нужен.
- Персонажи: guest, Reader, Author-владелец, другой Author, Moderator, Admin;
  Reader с прежними файлами после отзыва Author. JWT не помещать в команды/логи.
- Fixture corpus: 100 вложений (одинаковые/разные имена;1byte/50MiB),10 image
  sources и PNG/JPEG/WebP avatar; invalid/animated/metadata/orientation/alpha
  samples. Для файлов до/после restart/restore сохраняются контрольные hashes.

## Build и baseline

```powershell
dotnet tool restore
dotnet restore BlogEngine.slnx --locked-mode
dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror
dotnet format BlogEngine.slnx --verify-no-changes --no-restore
dotnet test tests/BlogEngine.UnitTests/BlogEngine.UnitTests.csproj -c Release --no-build
dotnet test tests/BlogEngine.IntegrationTests/BlogEngine.IntegrationTests.csproj -c Release --no-build
```

Ожидается: все применимые suites проходят без skipped/empty tests; старые
MVP сценарии сохраняются. Docker/Garage/PostgreSQL unavailable — failure,
а не успешный skip. NuGet pins и image digests совпадают с manifests/fixtures.

## Сценарии HTTP

1. Author: init upload с UUID Idempotency-Key → raw PUT → complete → status
   до Completed; metadata file и library содержат тот же fileId/name/size.
   Скачать original и сверить SHA-256. Повтор init/complete после потерянного
   ответа возвращает ту же сущность/исторический result без второй quota delta.
   Повтор ключа с другим payload —409. Другой PUT body принятой попытки —409.
2. Повторить для100 файлов, включая1byte и ровно50MiB, одинаковые имена;
   API+Garage restart и library download сохраняют100% bytes. Перевести часы
   дальше24h: готовая неприкреплённая библиотека остаётся. Library страницы
   20/max100 в createdAt/id DESC; совпадающее время не дублирует/теряет записи
   при обходе статического набора. Admin ownerId работает; чужой author — нет.
3. Автор статьи: назначить cover/inline/attachment своего owner, сохранить
   с X-Resource-Version. На Draft guest/чужой author получают404 и не узнают
   имя/размер. Publish открывает только display/attachment через эту статью;
   original publication image закрыт гостю и доступен owner/Admin без изменений.
   Download не меняет viewCount. Тот же файл в двух статьях живёт независимо
   от снятия одной связи. File delete при любой usage —409.
4. Unpublish/delete article/remove link: следующий HTTP запрос bytes получает
   404; старый адрес через60s также не разрешает новую передачу. Общий cache
   отсутствует; уже начатый разрешённый ответ может завершиться. Library file
   после delete article остаётся у owner. Внешняя cover/обычные Markdown links
   старого материала читаются без переноса.
5. Reader: GET avatar получает empty state/version; init Avatar с этой version,
   PUT/complete. Me/feed/detail/visible comment показывают один текущий avatar.
   Проверить PNG256/512, центр crop, orientation, alpha и отсутствие metadata.
   Замена/удаление немедленно меняет новые DTO и старые URL закрываются;
   гарантированный предел5min не превышен. Hidden/Deleted author=null.
6. Неудачная смена avatar (invalid body/storage/version conflict) сохраняет
   старый avatar. Admin не меняет чужой профиль. Replay старого completion
   после следующего swap возвращает исторический result и не меняет state.
7. Удалить неиспользуемый file: сразу исчезает из library, used уменьшается,
   download404, повтор owner DELETE204 без второй дельты. Outsider не узнаёт
   tombstone. Empty avatar DELETE с текущей version no-op; stale version409.

## Limits, negative и concurrency

Проверить size0/limit/limit+1 и ложный declaredSize; name scalar boundaries,
path/control; SVG/поломанный raster/mismatched format/APNG/animated WebP;
40млн/40млн+1 pixels и side16384/16385; небольшие image без увеличения для
статьи, avatar с увеличением. Недопустимые попытки не дают Ready/новый avatar.
Attachments arbitrary bytes скачиваются attachment/octet-stream/nosniff.

100-way barriertests: quota reserve/finalize/delete; first empty avatar init;
swap/delete одной версии; article edit одной version; link vs file delete;
complete vs cancel/expiry/cleanup. Допустимы документированные conflicts/
temporary capacity errors, но не превышение quota, lost update, две current
ссылки, active usage удалённого файла или stale-writer resurrection.
Exactly expiry24h резерв отсутствует даже при остановленном cleanup worker.
Отзыв Author между init/complete запрещает новый ordinary Ready; прежняя
library read/download/delete и own avatar сохраняются.

```powershell
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Core
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Concurrency
```

Скрипты выше создаются implement согласно operations contract; они сохраняют
counts, bounded race outcomes и SQL/EXPLAIN в игнорируемый `artifacts/`.
Изменяемое поведение проверяется через настоящий HTTP pipeline, отдельно
реальный schema/binding до documentary OpenAPI overlay. Deliberate DTO drift
должен провалить contract test. JSON8MiB limit не повышен; binary50MiB проходит
с bounded RAM/spool, корректным Accept и cancellation.

## Faults, cleanup и recovery

```powershell
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Faults
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Cleanup
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Restore
```

Отдельный fault test завершает уже принятый Garage PUT после истечения lease
и успешного cleanup DeleteObject: periodic reconciliation Retired/Deleted
object tombstone обнаруживает поздние bytes и удаляет их повторно.

Fixture управляемо прерывает PUT/download, теряет ответ после S3 PUT/SQL commit,
останавливает worker после создания object plan, роняет Garage/заполняет storage,
удаляет/портит объект при Ready metadata. Ожидается503 до success headers при
missing/corrupt data, сохранение прежнего avatar/файлов, однозначный status
и safe retry. Специально замедлить Garage GET и закрыть publication до headers:
повторная проверка запрещает выдачу. После успешных headers обрыв не превращается
в ложное полное скачивание (Content-Length/hash у клиента не совпадает).

Управляемыми часами проверить expiry24h и cleanup≤24h после retirement/восстановления
зависимостей. Старые avatar source/objects, cancelled/rejected uploads и deleted
files физически удалены; ready library/current avatar/publication source целы.
Проверить fencing, истёкшие leases, restart и несколько API replicas;
не использовать реальные многочасовые sleeps. Backlog test измеряет throughput,
чтобы задание не обещало deadline только на основании интервала таймера.

Restore scenario создаёт согласованный recovery set по operations runbook,
восстанавливает SQL+все Garage volumes в чистую среду и сверяет hashes/links/
avatars/quota/права. Production topology отдельно проверяет потерю одного узла
из трёх domains и последующее восстановление. Single-node fixture этой
проверки production не заменяет. Upgrade/rollback rehearsal повторяет restore
с закреплёнными версиями; произвольный откат image не считается проверкой.

## Migration/readiness и критерии выхода

Существующий `scripts/validation/Invoke-MigrationValidation.ps1` расширяется
Media chain и запускается штатным интерфейсом из документации database.
Нужны empty DB и upgrade с MVP dataset, constraints/grants, отсутствие DDL
у API, отсутствие Media write у Operator. Startup migration отсутствует.
Readiness падает при несовместимой schema, bucket/sentinel недоступности
и отсутствии spool capacity; liveness при этом остаётся здоровым.
Логи/артефакты не содержат tokens, original names, byte content и S3 secrets.

| Success criterion | Основное доказательство |
|---|---|
| SC-001 |100 byte-exact files +10 закрытых image sources, restart/cleanup |
| SC-002 |полная role/state/variant матрица и повторная pre-header authorization |
| SC-003 |Reader avatar cycle, PNG/metadata, author projections/tombstones |
| SC-004 |limits corpus и100-way controlled races |
| SC-005 |fault injection, unknown-result replay, no false Ready |
| SC-006 |exact expiry/retirement и backlog recovery |
| SC-007 |согласованный restore с hashes/links/access |
| SC-008 |полный применимый MVP regression suite/legacy clients |

Выход: все применимые проверки прошли, нет skipped обязательных scenarios,
pins/digest/grants/runbooks и evidence зафиксированы, поддерживаемые v1 consumers
переведены на aggregate contract. Иначе implementation не завершена.
