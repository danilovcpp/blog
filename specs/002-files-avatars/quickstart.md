# Quickstart: validation файлов и аватаров

Эти сценарии предназначены для приёмки реализации. Для их запуска потребуются
новые endpoints, Media fixtures и validation scripts.
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
   Повтор ключа с другим payload возвращает 409. Повтор PUT с той же длиной
   и hash возвращает 204. Другой payload в пределах лимита даёт 409, а
   превышение лимита назначения даёт 413. В обоих случаях `ContentReceived`,
   staging bytes и резерв сохраняются. Complete подтверждает первоначальные
   байты.
2. Повторить для100 файлов, включая1byte и ровно50MiB, одинаковые имена;
   API+Garage restart и library download сохраняют100% bytes. Перевести часы
   дальше24h: готовая неприкреплённая библиотека остаётся. Library страницы
   20/max100 в createdAt/id DESC; совпадающее время не дублирует/теряет записи
   при обходе статического набора. Проверить пустой набор, последнюю и
   запредельную страницу, невалидные параметры и текущий порядок страницы
   после добавления или удаления файла между запросами. Admin ownerId
   работает; чужой author — нет.
3. Автор статьи: назначить cover/inline/attachment своего owner, сохранить
   с X-Resource-Version. На Draft guest/чужой author получают404 и не узнают
   имя/размер. Publish открывает только display/attachment через эту статью;
   original publication image закрыт гостю и доступен owner/Admin. Для 10
   закрытых исходников сравнить исходное имя скачивания и SHA-256 до и после
   перезапуска API/Garage и выполнения очистки.
   Download не меняет viewCount. Тот же файл в двух статьях живёт независимо
   от снятия одной связи. Одно изображение можно назначить обложкой и
   иллюстрацией. Каждая связь считается в своей роли, повтор в одной роли
   отклоняется. File delete при любой usage —409.
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
   tombstone. `DELETE /media/files/{id}` для текущих и бывших Avatar IDs
   возвращает 404 владельцу и Admin. Удаление аватара доступно через
   версионированный маршрут профиля. Удаление пустого аватара с текущей
   версией возвращает прежнее состояние, с устаревшей версией даёт 409.
8. С назначенным аватаром автора выполнить create/edit/publish/unpublish и
   команды комментариев, возвращающие автора. Ответ содержит текущий аватар
   из согласованного снимка команды. Проверить конкурентную замену аватара
   или изменение публикации после коммита, чтобы ответ команды сохранял свой
   снимок и не собирался повторным запросом после коммита.

## Limits, negative и concurrency

Проверить size0/limit/limit+1 и ложный declaredSize; name scalar boundaries,
path/control; SVG/поломанный raster/mismatched format/APNG/animated WebP;
40млн/40млн+1 pixels и side16384/16385; небольшие image без увеличения для
статьи, avatar с увеличением. Недопустимые попытки не дают Ready/новый avatar.
Attachments arbitrary bytes скачиваются attachment/octet-stream/nosniff.

В настоящем HTTP pipeline проверить PUT с Content-Length и chunked transfer.
Для первой подходящей попытки в `AwaitingContent` несовпадение размера в
пределах назначения даёт `400 validation_failed`, превышение предела
назначения даёт `413 request_too_large`. API фиксирует `Rejected` и немедленно
освобождает резерв. На референсном ingress с лимитом 51 MiB отправить Attachment
50 MiB + 1 байт и подтвердить, что ответ формирует API. Отдельно превысить
лимит ingress. GET upload и GET allowance показывают прежнее состояние и
резерв. Затем доступен корректный PUT либо отмена. Прерванное чтение остаётся
повторяемым при отсутствии установленной ошибки размера. Повтор принятого
PUT проверяется сценарием 1.

Для 100-way barrier tests использовать подходящие роли и данные, заранее
подготовить объекты и обеспечить ресурсы тестовой среды для входящих запросов.
Транспортная ёмкость не должна подменять проверку доменных конфликтов.

- Для 100 равных запросов резерва с разными idempotency keys оставить место
  ровно под 20. Ожидается 20 новых резервов и 80 `quota_exceeded`. Проверить также гонки
  reserve/finalize/delete без превышения квоты.
- Для 100 изменений одной версии публикации и отдельно для 100 изменений
  одной версии аватара подготовить действительные разные команды. В каждой
  серии фиксируется ровно одно изменение. Остальные команды завершаются
  конфликтом версии, для фонового назначения аватара проверить итоговый
  `errorCode` попытки. При конкурентной инициализации пустого аватара
  проверить единую версию.
- Для attach/delete проверить оба порядка под управлением барьеров и затем
  повторить под нагрузкой. Если первой зафиксирована привязка, удаление
  отклоняется. Если первым зафиксировано удаление, привязка отклоняется.
  После каждой гонки отсутствуют ссылки на удалённые файлы.
- Complete vs cancel/expiry/cleanup проверяется с барьерами. Просроченный
  или отменённый upload не возвращается в Ready даже при запоздалой записи.

Exactly expiry24h резерв отсутствует даже при остановленном cleanup worker.
В двух API hosts и с отдельным writer Accounts барьерами проверить обе
очередности отзыва Author и коммита Ready. Если отзыв закоммитился первым,
новый обычный файл не становится Ready и резерв освобождается. Если Ready
закоммитился первым, исторический результат остаётся доступен. Так же
проверить конкуренцию отзыва с командой публикации, которая требует роль и
записывает управляемые связи. Прежняя library read/download/delete и own
avatar остаются доступны владельцу.
После отзыва Author новая попытка PUT обычного файла даёт `403 forbidden`
до чтения тела и сохраняет состояние и резерв. Если PUT получил разрешение
до отзыва, передача может сохранить staging bytes. Последующий complete
отклоняет активную попытку и освобождает резерв. Ready не появляется.

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

В API container с лимитом памяти 1 GiB одновременно запустить четыре
передачи вложений по 50 MiB и одно декодирование около допустимого предела. Зафиксировать
измеренный пик памяти контейнера ниже лимита, успешные операции без OOM и
отсутствие буферизации полного тела. Проверить отдельно ограничение spool.

## Faults, cleanup и recovery

```powershell
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Faults
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Cleanup
& .\scripts\validation\Invoke-MediaValidation.ps1 -Scenario Restore
```

Отдельный fault test завершает уже принятый Garage PUT после истечения lease
и успешного cleanup DeleteObject. Повторная проверка Retired/Deleted object
записи обнаруживает поздние bytes и удаляет их повторно.

Fixture управляемо прерывает PUT/download, теряет ответ после S3 PUT/SQL commit,
останавливает worker после создания object plan, роняет Garage/заполняет storage,
удаляет/портит объект при Ready metadata. Ожидается503 до success headers при
missing/corrupt data, сохранение прежнего avatar/файлов, однозначный status
и safe retry. Замедлить Garage GET и закрыть публикацию до headers. Проверить
как успешное чтение, так и сбой Garage GET после начала запроса. В обоих случаях
повторная авторизация возвращает 404. Повторить при удалении обычного файла
и замене аватара во время сбоя GET. При сохранении доступа сбой GET даёт 503.
Отдельно отозвать Admin во время успешного и неуспешного чтения чужого
private файла или черновика. Повторная проверка читает актуальные локальные
роли и возвращает 404 в обоих случаях.
После успешных headers обрыв виден клиенту по несовпадению длины/hash.

Принудительно остановить API процесс после начала записи во временный файл.
Запустить его замену на том же постоянном spool volume. До readiness и приёма
передач освобождается место от файлов прежнего процесса, после чего в пределах
бюджета проходит новая передача. Проверить, что параллельный API host со своим
томом сохраняет свою активную передачу. Попытка захватить его занятый том
оставляет readiness отрицательной и сохраняет активный временный файл.

Управляемыми часами проверить expiry24h и cleanup≤24h после retirement/восстановления
зависимостей. Старые avatar source/objects, cancelled/rejected uploads и deleted
files физически удалены; ready library/current avatar/publication source целы.
Проверить fencing, истёкшие leases, restart и несколько API replicas с
управляемыми часами. Сценарий Cleanup воспроизводит исходную нагрузку из
[operations](contracts/operations.md#health-logs-и-cleanup). При одном worker
и цикле 5min загрузить 10 000 сохранённых Retired/Deleted записей, включая
pending и постоянные completed tombstones, и 1 000 необработанных истёкших
загрузок, в том числе без объектов. Подавать до 1 000 новых retirements и
до 1 000 новых expiries за скользящие 24h. Объекты, выведенные из использования
при обработке истёкших загрузок, входят в лимит retirements. Измерить прогресс
трёх очередей при лимите 100 записей каждой за цикл. Доказать полный обход
за 12h и удаление в исходный 24h срок после
retirement либо восстановления, включая PUT, успешно завершившийся в первый
час исходного срока после уже выполненного DeleteObject. Для позднего
появления после первого часа проверить отдельные 24h с более позднего из
моментов повторного появления и восстановления доступности зависимостей.
Зафиксировать объём, продолжающийся приток, максимальные задержки и результат
проверки alert при превышении нагрузки или 12h между проверками.
Для целевой топологии отдельно измерить устойчивую пропускную способность
и задержки на закреплённых зависимостях в реальном времени. Ускоренный прогон
CI проверяет границы и переходы с управляемыми часами.

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
Негативные проверки БД подставляют файл того же владельца с неверным
назначением или состоянием, а также объект того же файла с неверным kind
или state. Подтвердить, что БД отклоняет такие активные ссылки, а корректные
переходы удаления и замены по-прежнему завершаются атомарно.
Readiness падает при несовместимой schema, bucket/sentinel недоступности
и при занятом, неочищенном или исчерпанном spool; liveness остаётся здоровым.
Логи/артефакты не содержат tokens, original names, byte content и S3 secrets.

Репетиция переключения следует [operations](contracts/operations.md#переключение-на-media).
До запуска совместимого backend с записью закрыть внешнюю запись и входящие
запросы к старому backend, включая GET. Завершить старые запросы и подтвердить,
что старые writer-сессии закрыты, а повторное подключение с прежними
credentials не даёт права записи. Проверить readiness и consumer smoke при
закрытой внешней записи, затем открыть её совместимому backend. Зафиксировать
ёмкость очистки на целевой топологии до активации и при изменении масштаба.

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
