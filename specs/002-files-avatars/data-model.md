# Data model: файлы и аватары

Проект PostgreSQL schema; реализация — additive EF migration после
`AddViewDeduplication`. UUID IDs, timestamptz UTC, bigint lengths,
32-byte SHA-256. API DTO отделены от EF. Keys/leases/hashes не публичны.

## Владение и связи

Media владеет `files`, `uploads`, `allowances`, `avatar_states`,
`publication_usages`, `objects`, `representations`, `cleanup_jobs` в schema
`media`. Accounts сохраняет `local_users`; Publishing — публикации и их
семантику. Registrations обновляются через Media.Contracts в publication tx.
FK между схемами защищают данные, но не разрешают обход бизнес-контрактов.

```text
local_user 1 ─ * File; 1 ─ 0..1 Allowance; 1 ─ 0..1 AvatarState
Upload 1 ─ 1 File; Upload 1 ─ * ObjectRecord
File 1 ─ * Representation; File 1 ─ * PublicationUsage ─ 1 Publication
AvatarState ─ 0..1 current File; ObjectRecord 1 ─ 0..1 CleanupJob
```

## File — `media.files`

| Поле | Ограничения/смысл |
|---|---|
| id | PK, стабильный непрозрачный fileId |
| owner_id | NOT NULL FK local_users; immutable |
| purpose | CHECK Attachment / PublicationImage / Avatar |
| original_name | trim, 1..255 Unicode scalars, не путь |
| declared_content_type | NOT NULL, канонический media type без параметров |
| original_size | >0, ≤50/10/5 MiB по purpose |
| verified_content_type | nullable до проверки; JPEG/PNG/WebP для image |
| state | Pending / Ready / Deleted |
| created_at, ready_at, deleted_at | CHECK согласованности со state |
| original_object_id | FK objects; Ready Attachment/PublicationImage требует source |
| version | UUID внутренний concurrency token |

Готовые bytes/проверенные поля неизменны. Avatar source очищается после
назначения; original_size остаётся для квоты. Deleted — компактный ownership
tombstone для идемпотентных DELETE/complete без доступа к bytes/metadata.
Имя не используется в key. Domain проверяет Unicode scalars, path separators,
controls; DB дополнительно проверяет непустое имя, длину, размеры/state.
Scalar count нельзя заменять числом UTF-16 code units.

## Upload — `media.uploads`

| Поле | Ограничения/смысл |
|---|---|
| id, file_id, owner_id | PK; UNIQUE file_id; FK file/owner |
| idempotency_key, request_fingerprint | UNIQUE(owner_id,key); fingerprint включает payload и expected avatar version |
| state | AwaitingContent / ContentReceived / Processing / Completed / Rejected / Cancelled / Expired |
| created_at, expires_at | expires_at=created_at+24h, immutable |
| reserved_bytes | declared original_size; считается только active и expires_at>now |
| current_object_id | FK objects, выбранный accepted attempt |
| content_sha256, actual_size | проверенные bytes PUT |
| expected_avatar_version | обязательна для Avatar, иначе null |
| lease_generation, lease_until | fencing PUT/processing, не продлевает expiry |
| next_attempt_at, attempts | retry с bounded backoff |
| result, error_code | immutable completion snapshot / стабильная terminal причина |

Owner/key/result tombstones сохраняются: replay init/complete не создаёт
второй файл и не переназначает прежний avatar. Изменение retention требует
явного пересмотра идемпотентности, не является произвольной очисткой.

```text
AwaitingContent → accepted PUT → ContentReceived → complete → Processing
Processing → verified objects + guarded SQL commit → Completed (File Ready)
active → invalid bytes / revoked rights / avatar conflict → Rejected
active → cancel → Cancelled
active AND DB now >= expires_at → Expired
```

Temporary I/O failure сохраняет возможность retry; sync boundary даёт 503,
worker использует внутренний next_attempt_at. Принятые PUT bytes менять нельзя: тот же hash →204,
другой →409. Size/format mismatch терминально отклоняет попытку и освобождает
резерв. Completed отменять нельзя; применяется file/avatar operation.
Worker crash после S3 безопасен за счёт object plans/generation; stale writer
не может закоммитить Ready после expiry/delete. Completion replays возвращают
исторический результат даже после удаления файла или следующей смены avatar.

После проверки владельца точный replay существующего init/Completed result
выполняется до guards expiry/актуальной роли/avatar version: они относятся
к новому переходу, не отменяют уже зафиксированный исторический результат.
GET вычисляет effective Expired по DB time независимо от worker; запись
использует тот же deadline. Отказ complete активной обычной загрузки после
отзыва роли терминален: Rejected/forbidden и немедленное освобождение резерва.

## Allowance — `media.allowances`

owner_id PK/FK, limit_bytes CHECK>0 default 1,073,741,824,
used_bytes CHECK 0..limit. Lazy INSERT ON CONFLICT создаёт одну lock row.
Reserved = SQL SUM active uploads WHERE expires_at>DB clock; отдельный
запаздывающий reserved counter не нужен. Все reserve/ready/delete/avatar
и registrations берут owner row lock. Available=limit-used-reserved.
Exactly expiry исключает резерв независимо от очистки. Ready commit повторно
проверяет deadline и меняет reserve→used один раз. Avatar swap атомарно
вычитает старый original_size и добавляет новый. До swap резервируется весь
новый размер; derived bytes не учитываются. Original_size avatar считается
до замены, хотя source уже очищен. Межстрочную сумму защищает owner lock,
а не выдуманный row CHECK; нужны 100-way integration tests.

## AvatarState — `media.avatar_states`

user_id PK/FK; current_file_id nullable FK files, UNIQUE когда не null;
version NOT NULL UUID; updated_at NOT NULL. Empty state также имеет version.
Собственный GET avatar/me создаёт state через INSERT ON CONFLICT; concurrent
bootstrap сходится на одной version. Публичный AuthorRef для старого user
без state возвращает avatar=null без побочного создания профиля.

Меняет только текущий verified user, Admin не исключение. Новый Ready avatar,
обе representations и state/version коммитятся вместе. Conflict оставляет
старый state; новый pending file rejected, objects идут в cleanup. Старый
file при swap становится Deleted и списывается из used в том же commit.
Delete пустого state с current version — no-op200 без смены version;
stale version всегда409. Public URL проверяет current reference, поэтому
физическая retention24h не продлевает публичный доступ старого avatar.
Composite FK(current_file_id,user_id) → files(id,owner_id) дополнительно
закрепляет владение в DB; domain проверяет purpose=Avatar и Ready.

## PublicationUsage — `media.publication_usages`

publication_id FK publications; file_id FK files; publication_owner_id;
role CHECK Cover/Inline/Attachment; position>=0.
PK(publication_id,role,file_id); UNIQUE(publication_id,role,position);
partial UNIQUE(publication_id) WHERE role=Cover.

Media domain/tx проверяет max1/50/20, purpose, Ready и owner под locks.
Для ownership: files UNIQUE(id,owner_id), composite usage FK(file_id,publication_owner_id) -> files(id,owner_id);
publications UNIQUE(id,author_id), composite usage FK(publication_id,publication_owner_id) -> publications(id,author_id).
Cross-row counts и cover source XOR (external URL / managed usage) защищены
одной domain/SQL операцией и integration tests; одиночный CHECK их не выражает.
Publication version guard распространяется на все регистрации; ошибка
откатывает текст, classification и весь набор ссылок. Delete publication
снимает usages в своей tx, сохраняя library files. Delete file под owner lock
отказывает при usage/current avatar. Markdown URL usage не создаёт.

## Objects, representations и cleanup

`media.objects`: id PK; upload_id/file_id FK; bucket_kind Originals/Renditions;
object_key UNIQUE в bucket; attempt_generation; kind Source/Display/Avatar256/
Avatar512; state Planned/Present/Retired/Deleted; verified length/hash для
Present; created_at/retired_at, generation/lease, next_reconcile_at и
last_reconciled_at для bounded обхода tombstones. Новый attempt — новый key.
ObjectRecord сохраняется **до** S3 PUT: неизвестный исход имеет tracked cleanup.

`media.representations`: PK(file_id,kind), UNIQUE object_id FK objects,
width/height>0, mime=image/png, verified length/SHA-256. Display≤2560;
Avatar256/512 точные квадраты. Ready publication image требует Display+Source,
current avatar — обе PNG; attachment — Source. Source publication не является
публичной representation; owner/Admin скачивает исходные bytes/name.

`media.cleanup_jobs`: id PK, object_id UNIQUE FK; not_before/due_at;
attempts/next_attempt_at; generation/lease_until; completed_at, error_code.
Retirement+job создаются в той же tx, что logical delete/cancel/reject/swap.
Перед S3 DeleteObject проверяются отсутствие активной ссылки и generation;
S3 выполняется вне tx, затем фиксируется успех. Missing object — успех.
Поздний fenced writer может оставить только garbage; job перепроверяет
объект после конца transfer lease и повторяет delete. Timeout/отмена SDK не
гарантирует остановку уже принятого Garage PUT. ObjectRecord tombstone
сохраняется, periodic reconciliation запускается каждые5min для Retired/Deleted
objects даже при completed_at cleanup job: поздно появившийся объект
удаляется снова. Сроки FR-028 включают этот backlog; объём/throughput
проверяются fault-тестом позднего PUT после успешного DeleteObject.
Сроки cleanup включают этот drain; нельзя считать job завершённым до него.
Ready library никогда не выводится из отсутствия usage или возраста.

## Locks, indexes и migration

Upload использует composite FK(file_id,owner_id) → files(id,owner_id).
ObjectRecord хранит согласованную пару upload/file через composite FK к
UNIQUE uploads(id,file_id). Representation и original_object reference
используют composite FK к UNIQUE objects(id,file_id), чтобы объект не мог
оказаться источником/представлением другого файла. Nullable source reference
позволяет создать Pending file, затем object plan, затем связать их без
отключения FK. Эти ограничения также проверяются negative persistence tests.

Порядок: publication при наличии → allowance owner → avatar state → files
по UUID → uploads/jobs. Worker claim не держит job lock при получении более
ранних locks; claim — отдельная короткая tx с lease, finalization — общий порядок.
Никакой network/image operation внутри tx. Public file access проверяет
coherent publication+usage+file snapshot повторно после verified spool.

- files(owner_id,created_at DESC,id DESC) WHERE Ready AND purpose!=Avatar.
- uploads(owner_id,expires_at) WHERE active; UNIQUE(owner,idempotency_key).
- usages(file_id), usages(publication_id,role,position).
- cleanup(next_attempt_at,id) WHERE incomplete; processing uploads аналогично.
- objects(next_reconcile_at,id) WHERE Retired/Deleted; bounded batches с
  приоритетом due cleanup и oldest reconciliation, без полной материализации.
- avatar current_file unique partial, необходимые FK query indexes.

Migration добавляет Media schema/tables и publication composite unique,
не требует backfill avatar и сохраняет внешние covers/Markdown. Проверяются
empty DB, upgrade с MVP dataset, SQL locks/duration, ограничения и grants.
Runtime не имеет DDL; Operator не получает Media grants. Startup migrations
запрещены. Rollback сохраняет additive schema и закрывает старый writer;
restore bytes/schema/links выполняется общим recovery set.
