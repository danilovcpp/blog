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
| Media:Spool:Directory | непубличный путь, только процессу; не webroot |
| Media:Spool:BudgetBytes | 1,073,741,824 по умолчанию, validated positive |
| Media:Transfers:Concurrency | 4 на API replica, shared upload/download semaphore |
| Media:Transfers:TotalTimeout | 120s, bounded body/spool/response operation |
| Media:Processing:Concurrency / BatchSize | 1 / 20 на replica; persistent job leases |
| Media:Processing:LeaseDuration | 180s, renewal каждые30s, fenced generation |
| Media:Cleanup:Interval / BatchSize | 5min / 100; backlog throughput доказывается тестом |
| Media:Cleanup:RetryMaxDelay | 15min; retry после outage, bounded jitter |
| Media:MaintenanceMode | закрывает mutations/worker claims, readiness=false; liveness=true |

Продуктовые limits50/10/5MiB,1GiB,24h,1/50/20 — правила спецификации,
не произвольные настройки, обходящие приёмку. Изменение требует обновления
spec/contracts/tests. Per-route body limits соответствуют purpose/declaredSize;
глобальный JSON envelope остаётся8MiB. reverse proxy ingress body limit для
binary route≥50MiB; HTTP headers и лишние bytes ограничиваются отдельно.

S3 client: path-style, SigV4, no chunked PUT, known length/Content-MD5,
WHEN_REQUIRED request/response SDK checksums. Реальная совместимость exact pins
проверяется до release, включая cancellation/timeout/truncation. Native Skia
загружается в целевом Linux image; отсутствие codec/native library — startup
или readiness failure, не скрытый fallback. Изменения SDK требуют повторной проверки.

## Health, logs и cleanup

`/health/live` проверяет процесс. `/health/ready` дополнительно к текущим
PostgreSQL/JWKS проверяет совместимость Media schema и runtime read grants,
sentinel GET обоих buckets и writable/free spool budget. Probe не применяет
DDL и не требует доступа API к migration history; выбирается совместимый
schema marker с минимальным SELECT grant. Публичные health ответы общие;
диагностика только в защищённых логах.

Structured ILogger: traceId, uploadId/fileId/jobId, attempt, stable error code,
duration/size и backlog age. Не логировать raw bodies, original names,
Identity tokens, S3 keys/credentials/URL query signatures. Metrics: used/reserved,
free spool, active transfers/decodes, oldest cleanup age, retry count, integrity
failures, missing-object count и storage/readiness errors. Alert до угрозы24h
deadline (например oldest pending>12h) с учётом причины outage.

Все objects имеют заранее записанный план и уникальный attempt key. Retired
objects и cleanup jobs создаются атомарно с logical change. Cleanup не удаляет
Ready library, сохранённый source publication image или current avatar.
Worker leases/fencing и bounded SDK calls предотвращают публикацию stale
result. Очистка ждёт завершения возможных writers, повторяет missing/delete
и reconciliation Retired/Deleted object records каждые5min, включая objects
с уже завершённым cleanup job: принятый Garage PUT может завершиться после
отмены SDK и успешного удаления. Tombstones остаются для повторного удаления.
Неполный download не выдаётся как
полный успех: spool/hash до headers; обрыв после headers завершает соединение,
а клиент проверяет Content-Length. Host shutdown отменяет операции, освобождает
spool и оставляет durable jobs для retry, без fire-and-forget.

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
   потребность места измеряется, RPO равен фактическому backup interval,
   численный RTO до rehearsal не обещается.
7. Запустить Garage и API, проверить readiness/неизменность контрольного набора,
   затем открыть writes. При ошибке сохранить maintenance и диагностировать.

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
После readiness и consumer smoke допускается activation. Spool не входит
в recovery set и создаётся чистым; его содержимое не единственная копия Ready bytes.

Upgrade: staging rehearsal с реальными данными-копиями, release notes/migration
guide выбранной версии, новый exact digest и новый согласованный backup перед
записью нового формата. Проверить отказ узла, restore и совместимость S3.
Rollback Garage после изменения формата — восстановление всех частей прежнего
набора, не только прежний image. Если после backup были новые записи, их потеря
при restore явно оценивается оператором до действия. Database production Down
не выполняется. Старый backend не допускается к записи managed links; допустим
maintenance/read-only rollback с сохранением additive schema.

## Планируемые validation script interfaces

При implement создаются `scripts/validation/Invoke-MediaValidation.ps1`
с параметрами `-Scenario All|Core|Concurrency|Faults|Cleanup|Restore`
и `-OutputDirectory` (default `artifacts/validation/media`), а также
`Invoke-MediaRecoveryValidation.ps1 -RecoverySetDirectory -OutputDirectory`.
Они работают с изолированными fixtures, возвращают nonzero при пропуске,
недоступной зависимости, пустом suite или несоответствии manifest; секреты
не являются параметрами/выводом. Операционные production backup команды
описываются отдельно в runbook реализации с фактическими путями inventory.
