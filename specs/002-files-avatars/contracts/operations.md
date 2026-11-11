# Operational contract: Media/Garage

Это проект обязательных deployment/runbook интерфейсов. Скрипты и конфигурация
создаются при implement; сейчас сервис Garage не развёрнут и restore не проверен.
Решения и официальные источники — [research.md](../research.md).

## Deployment и доступ

- Production: Garage v2.5.0, три узла в трёх независимых failure domains,
  replication_factor=3, consistency_mode=consistent. До activation inventory
  содержит domains, volume paths/capacity, закреплённый image manifest digest
  и успешную проверку потери одного узла. Три containers одного VPS не HA.
- Dev/integration: single-node replication1, отдельный Compose profile `media`;
  только loopback/private network. Та же версия/digest, API operations и schema.
- Metadata/data — отдельные постоянные пути; их cleanup не выполняется
  container lifecycle. RPC/Admin доступны только storage/operator network.
  S3 endpoint доступен Blog и provisioning tools, не публичному браузеру.
- Два private bucket, `blog-originals` и `blog-renditions` по умолчанию;
  website/public read не включается. Имена можно настроить, но не выбирать
  клиенту. Runtime key имеет только нужные read/write/delete на этих buckets;
  provisioning/admin key отделён и отсутствует в API container.
- Provisioner создаёт в каждом bucket sentinel с известными bytes/hash для
  readiness. API читает sentinel ограниченным GET, не создаёт/удаляет объекты
  при probe. Пользовательские файлы не являются health fixture.
- PostgreSQL: прежний major18, UTF-8/C/UTC, API DML только Media и необходимые
  read contracts, migration principal отдельно. Operator Media не меняет.
  Additive schema migration/grants выполняются отдельным deployment job.

## Переключение на Media

1. Применить проверенные additive migration и grants, подготовить private
   buckets и sentinels, проверить закреплённые images и план перехода
   потребителей. Зафиксировать результат проверки ёмкости очистки для
   целевой топологии.
2. Закрыть внешнюю запись и приём новых запросов старым backend, включая GET
   с возможной регистрацией пользователя. Дождаться
   завершения его запросов, остановить старые writer-процессы и отозвать их
   доступ к записи. Проверить отсутствие старых сессий записи и невозможность
   записи со старыми credentials при повторном подключении. Совместимый
   backend получает credentials, недоступные прежнему. Завершить этот шаг
   до запуска нового backend с записью и до первой управляемой связи. Если
   план возврата предусматривает старый writer, сохранить recovery set
   после миграции и до запуска нового backend с записью.
3. Запустить совместимый API с закрытой внешней записью. Завершить переход
   поддерживаемых v1 consumers. Если включён MaintenanceMode, снять его при
   закрытой внешней записи, чтобы проверить readiness и consumer smoke.
4. Открыть запись только для совместимой версии после успешных проверок.
   Сохранить порядок переключения и результат проверки запрета старых
   writers в rollout evidence. Правила возврата к прежней версии приведены
   в разделе восстановления.

## Configuration contract

Options валидируются при старте, placeholders/пустые обязательные значения
отклоняются. В репозитории только имена и безопасные примеры без секретов.

| Options key | Правило |
|---|---|
| Media:Storage:Endpoint | trusted absolute HTTPS; dev HTTP только явным local/private opt-in |
| Media:Storage:Region | `garage` |
| Media:Storage:OriginalsBucket / RenditionsBucket | разные private buckets, provisioned заранее |
| Media:Storage:AccessKey / SecretKey | защищённый provider, никогда в логах/CLI args |
| Media:Storage:AllowLocalHttp | false production; true только dev/integration |
| Media:Storage:RequestTimeout | 60s per bounded SDK operation, отмена от host/request |
| Media:Storage:ReadinessTimeout | 3s, без retries внутри probe |
| Media:Spool:Directory | непубличный постоянный том, закреплённый за одной API replica |
| Media:Spool:BudgetBytes | 1,073,741,824 по умолчанию, validated positive |
| Media:Transfers:Concurrency | 4 на API replica, shared upload/download semaphore |
| Media:Transfers:TotalTimeout | 120s, bounded body/spool/response operation |
| Media:Processing:Concurrency / BatchSize | 1 / 20 на replica; persistent job leases |
| Media:Processing:LeaseDuration | 180s, renewal каждые30s, fenced generation |
| Media:Cleanup:Interval / BatchSize | 5min / 100; независимый лимит пакета для due object jobs, expired uploads и reconciliation |
| Media:Cleanup:RetryMaxDelay | 15min; retry после outage, bounded jitter |
| Media:MaintenanceMode | закрывает команды изменения и новые worker claims, readiness=false, liveness=true. GET с bootstrap остаются возможны до полной остановки hosts |

Продуктовые лимиты 50/10/5 MiB, 1 GiB, 24h и 1/50/20 задаются спецификацией.
Изменение требует обновления spec/contracts/tests. Для raw PUT предел тела
на API определяется назначением файла, а `declaredSize` используется для
проверки точного размера в пределах этого лимита. Глобальный предел JSON
остаётся 8 MiB. Для raw PUT настроить ingress выше максимума API, исходное
значение 51 MiB (53 477 376 байт). Если ingress отверг запрос до API, состояние
upload и резерв сохраняются. Потребитель проверяет статус через GET и может
повторить PUT либо отменить загрузку.

Каждому одновременно работающему API host нужен собственный spool volume
вне webroot. При повторном подключении тома новый процесс получает
исключительное владение директорией до её очистки. До readiness и приёма
передач он удаляет временные файлы предыдущего процесса и проверяет доступную
ёмкость. При занятом томе или ошибке очистки readiness остаётся отрицательной,
передачи блокируются. Файлы spool не содержат единственной копии Ready bytes.

Референсный лимит памяти API container — 1 GiB. При нём проверяется
одновременная работа четырёх передач и одного декодирования изображения
около допустимого предела. Приёмка фиксирует пик памяти ниже лимита,
отсутствие OOM и потоковую запись без буфера размером с полное тело запроса
или скачивания. Бюджет spool учитывается отдельно.

S3 client: path-style, SigV4, no chunked PUT, known length/Content-MD5,
WHEN_REQUIRED request/response SDK checksums. Реальная совместимость exact pins
проверяется до release, включая cancellation/timeout/truncation. Native Skia
загружается в целевом Linux image; отсутствие codec/native library — startup
или readiness failure, не скрытый fallback. Изменения SDK требуют повторной проверки.

## Health, logs и cleanup

`/health/live` проверяет процесс. `/health/ready` дополнительно к текущим
PostgreSQL/JWKS проверяет совместимость Media schema и runtime read grants,
sentinel GET обоих buckets, успешную подготовку spool и writable/free spool
budget. Для проверки схемы probe выполняет SELECT совместимого schema marker
с минимальным grant. Подробности диагностики доступны в защищённых логах,
публичный health ответ сообщает состояние сервиса.

Structured ILogger: traceId, uploadId/fileId/jobId, attempt, stable error code,
duration/size и backlog age. Не логировать raw bodies, original names,
Identity tokens, S3 keys/credentials/URL query signatures. Metrics: used/reserved,
free spool, active transfers/decodes, oldest cleanup age, retry count, integrity
failures, missing-object count и storage/readiness errors. Для очистки также
измеряются количество Retired/Deleted записей и необработанных истёкших
загрузок, поступления за скользящие 24h, максимальный интервал повторной
проверки и пропускная способность всех трёх очередей. Настроить alert до
угрозы 24h (например oldest pending > 12h), при выходе за проверенную нагрузку
и превышении допустимой задержки повторной проверки. Причину outage
фиксировать отдельно.

Все objects имеют заранее записанный план и уникальный attempt key. Retired
objects и cleanup jobs создаются атомарно с logical change. Cleanup не удаляет
Ready library, сохранённый source publication image или current avatar.
Worker leases/fencing и bounded SDK calls предотвращают публикацию stale
result. Очистка учитывает активные lease и повторно удаляет объекты по
Retired/Deleted записям, включая объекты с завершённым cleanup job. Принятый
Garage PUT может завершиться после отмены SDK и успешного удаления, поэтому
tombstones сохраняются для повторной проверки.

Для первого этапа проверяется один cleanup worker с циклом 5min. В каждом
цикле он выделяет три независимых пакета до 100 элементов каждый. Первый
обрабатывает due object jobs, второй — истёкшие загрузки, третий повторно
проверяет Retired/Deleted object records. Прогресс каждой очереди сохраняется
между циклами. Повторные ошибки и новые элементы не вытесняют старые.

Проверяемая нагрузка включает до 10 000 сохранённых Retired/Deleted object
records на старте либо при восстановлении, включая pending jobs и постоянные
tombstones завершённых jobs, и до 1 000 новых retirements за любые 24h.
Очередь истёкших загрузок включает до 1 000 необработанных строк на старте
либо при восстановлении и до 1 000 новых expiries за любые 24h, в том числе
загрузки без объектов. Объекты, выведенные из использования при обработке
истёкших загрузок, входят в общий предел новых retirements.

При доступных зависимостях полный обход укладывается в 12h, а удаление
подлежащих очистке объектов — в 24h после утраты необходимости либо
восстановления зависимостей. Проверка включает продолжающийся приток и поздний
успешный PUT в первый час от исходного retirement либо восстановления, уже
после успешного DeleteObject.
Для объекта, вновь появившегося после первого часа этого срока, отдельные 24h
отсчитываются с момента повторного появления или восстановления доступности
зависимостей, если оно произошло позже.
CI проверяет сроки с управляемыми часами. Перед активацией и при изменении
масштаба измерить устойчивую пропускную способность и лаг всех очередей в
реальном времени с закреплёнными зависимостями и целевой конфигурацией.
Доказательство для рабочего профиля учитывает время фактических операций,
объём tombstones, pending jobs и expiries, а также продолжающийся приток.
При росте нагрузки подобрать ресурсы и подтвердить срок новым прогоном.

Успешная выдача требует проверенных длины и hash. Текущий доступ проверяется
перед успешным ответом и перед ответом об ошибке чтения. Обрыв после headers
завершает соединение, а клиент проверяет Content-Length. Штатная остановка
host отменяет операции, освобождает spool и оставляет durable jobs для retry.
После аварийной остановки применяется описанная выше очистка при запуске.

## Согласованная резервная копия

Для первого этапа используется **окно обслуживания**. Ниже порядок runbook,
а не утверждение о проверенном распределённом snapshot API Garage.

1. Выбрать recoverySetId и защищённое место вне трёх storage nodes. Зафиксировать
   версии/digests, schema migration ID, cluster layout и список volumes.
2. На ingress закрыть все mutations, включая Operator/maintenance tools;
   включить MaintenanceMode всех API replicas. Остановить processing/cleanup
   claims, дождаться in-flight requests/leases и завершить workers штатно.
   Затем остановить все API hosts и Operator, включая обработку GET:
   ActorResolver может создать local user, а GET me/avatar — empty state.
   Проверить отсутствие иных credentials/writers; одной фильтрации
   HTTP-методов POST/PUT/DELETE недостаточно.
3. Проверить Garage cluster healthy/synchronized, отсутствие pending repair;
   сверить Ready object manifest (key, length, SHA-256, file/representation id)
   с PostgreSQL. При отсутствующих объектах копия не считается согласованной.
4. Пока запись остаётся закрытой, выполнить `pg_dump` с deployment/backup
   credentials через защищённый provider. Сохранить dump, globals/roles/grants
   без публичного раскрытия секретов. Момент quiesce — логическая recovery point.
5. Остановить все Garage nodes штатно. Скопировать полные metadata/data volumes
   каждого узла вместе с config/layout и необходимыми защищёнными ключами.
   Не копировать live metadata DB произвольным файловым копированием.
6. Сформировать manifest: recoverySetId, quiesce time, checksums всех частей,
   pins, volume/node mapping, schema ID, object manifest. Encrypt backup и
   проверять целостность. Retention: исходно7 ежедневных и4 еженедельных наборов;
   потребность в месте измеряется. Фактический возраст последней проверенной
   точки определяет объём данных после неё, который потребует отдельной
   оценки при восстановлении. Численные цели RPO/RTO для первого этапа
   не заданы, фактическую длительность измеряют в rehearsal.
7. Запустить Garage и совместимый API с закрытой внешней записью. Снять
   MaintenanceMode, проверить readiness и неизменность контрольного набора,
   затем открыть writes. При ошибке держать внешнюю запись закрытой.

Факт завершения копирования не заменяет restore rehearsal. Garage replication
также не заменяет backup. Доступ к backup/secrets имеют только операторы recovery.

## Restore, upgrade и rollback

Restore выполняется в изолированную среду с **тем же** Garage/PostgreSQL/image
pins. Проверить manifest hashes; восстановить весь Garage node/volume layout
и SQL dump/grants из одного recoverySetId. До открытия writes проверить:
все Ready sources/representations доступны с прежними SHA-256, права/links/
avatars/quota согласованы, abandoned uploads не становятся Ready, Hidden/Deleted
не раскрываются. Expired uploads оцениваются по текущему времени; lease claims
сбрасываются безопасным recovery шагом, current generations сохраняются.
После readiness и consumer smoke открыть запись только совместимым версиям
по порядку переключения выше. Для восстановления подготовить чистый spool.

Upgrade: staging rehearsal с реальными данными-копиями, release notes/migration
guide выбранной версии, новый exact digest и новый согласованный backup перед
записью нового формата. Проверить отказ узла, restore и совместимость S3.
Rollback Garage после изменения формата — восстановление всех частей прежнего
набора, не только прежний image. Если после backup были новые записи, их потеря
при restore явно оценивается оператором до действия. Production rollback
сохраняет additive schema. После появления управляемых связей доступен
maintenance/read-only rollback с закрытой записью для старого backend.
Возврат старого writer допустим после согласованного восстановления SQL и
Garage из проверенного recovery set, созданного после additive migration и
до первой управляемой записи.

## Планируемые validation script interfaces

При implement создаются `scripts/validation/Invoke-MediaValidation.ps1`
с параметрами `-Scenario All|Core|Concurrency|Faults|Cleanup|Restore`
и `-OutputDirectory` (default `artifacts/validation/media`), а также
`Invoke-MediaRecoveryValidation.ps1 -RecoverySetDirectory -OutputDirectory`.
Они работают с изолированными fixtures, возвращают nonzero при пропуске,
недоступной зависимости, пустом suite или несоответствии manifest; секреты
не являются параметрами/выводом. Операционные production backup команды
описываются отдельно в runbook реализации с фактическими путями inventory.
