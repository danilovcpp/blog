# Implementation Plan: Файлы и аватары пользователей

**Branch**: `main` (фактическая Git-ветка; не изменялась)
| **Feature context**: `002-files-avatars` (BRANCH из setup-plan)
| **Date**: 2026-11-11 | **Spec**: [spec.md](spec.md)

**Input**: `specs/002-files-avatars/spec.md`, уточнения Q1–Q4 и
[source-constraints.md](references/source-constraints.md).

## Summary

Добавить серверную библиотеку файлов Author/Admin, изображения/вложения
публикаций и собственный аватар любого вошедшего пользователя. В существующий
модульный монолит вводится Media: метаданные, квоты, регистрации использования
и аватар — в PostgreSQL, неизменяемые объекты — в выбранном Garage.
Все buckets закрыты. Blog API принимает и выдаёт байты через ограниченные
дисковые временные файлы с проверкой длины/хеша; права проверяются при каждом
скачивании. Подключение UI/BFF и изменение прототипа не входят в реализацию.

## Technical Context

**Language/Version**: C#14/net10.0, SDK 10.0.402; существующие pins сохраняются.

**Primary Dependencies**: ASP.NET Core и EF Core 10.0.13; Npgsql/provider
10.0.3; AWSSDK.S3 4.0.104.12 + AWSSDK.Core 4.0.103;
SkiaSharp и SkiaSharp.NativeAssets.Linux.NoDependencies 4.155.0.
Новые pins/lock files добавляются при реализации; обоснование —
[research.md](research.md).

**Storage**: PostgreSQL 18.6 с существующим digest из `compose.yaml`, UTF-8/C/UTC;
Garage `dxflrs/garage:v2.5.0`, два закрытых bucket, постоянные metadata/data
volumes. Manifest digest Garage закрепляется до acceptance запуска во всех
deployment/test manifests, без floating tag. Spool — отдельный непубличный
volume с ограничением места и автоматическим удалением.

**Testing**: существующие xUnit v3/VSTest, Testcontainers PostgreSQL;
новая реальная Garage fixture с тем же image pin. HTTP-тесты через ApiFactory,
управляемые часы/барьеры, DB constraints, query inspection, fault injection,
migration/restore rehearsal. Подмены только для unit boundary tests.

**Target Platform**: Linux containers; PowerShell 7 для tooling. Production
Garage — три узла/три failure domains, replication=3, consistent; один узел —
только dev/integration. API replicas используют общую БД.

**Project Type**: расширение существующего ASP.NET Core REST web-service;
новые production-проекты и отдельный Media service не требуются.

**Performance Goals**: SC-001–SC-008, включая 100 конкурентных операций без
нарушения инвариантов. Новый latency/RPS SLA не вводится. Нет полной
RAM-буферизации файлов и N+1 по авторам. Библиотека 20/max100, порядок
createdAt/id DESC. Число data queries ограничено константой для страницы,
а не числом авторов/файлов; SQL/EXPLAIN сохраняются как evidence.

**Constraints**: 50/10/5 MiB; 40 млн пикселей/16384 на сторону; 1 GiB на
владельца; 1/50/20 связей cover/inline/attachment; upload expiry 24h;
очистка ≤24h при доступных зависимостях и после восстановления. Исходники
publication image сохраняются закрытыми; avatar source очищается.
No-store на выдаче соблюдает пределы 60 секунд/5 минут, проверяя каждый запрос.

**Scale/Scope**: один Blog, backend/contracts/operations. Стартовые limits
на API replica: 4 одновременных byte transfers суммарно, spool budget 1 GiB,
1 image decode worker, processing batch 20; API memory budget минимум 1 GiB.
User quota считает исходные размеры; физические производные планируются
отдельно. Budget exhaustion — временная ошибка с Retry-After. Limits
проверяются stress-сценарием и повышаются вместе с ресурсами оператора.

## Constitution Check

GATE до Phase 0: spec и существующая база проверены; противоречий и
запрашиваемых исключений нет. После Phase 1 проверены конкретные решения:

| Принцип | Решение и доказательство при реализации | До / после |
|---|---|---|
| I: модульность | Media владеет lifecycle/usage/avatar; Publishing вызывает Media.Contracts; обратного ребра нет; graph tests | PASS / PASS |
| II: C# | async/cancellation, Options, bounded workers; build/analyzers/format | PASS / PASS |
| III: данные | additive migration, FK/CHECK/UNIQUE, owner lock, UUID versions, короткие tx без S3; SQL/EXPLAIN и races | PASS / PASS |
| IV: API/security | OpenAPI delta + aggregate, resource authorization, ProblemDetails, binary route validation, закрытые buckets, consumer transition | PASS / PASS |
| V: тесты | unit инварианты, реальные PostgreSQL/Garage HTTP suites; negative/fault/migration/restore gates | PASS / PASS |
| VI: эксплуатация | validated options, least privileges, schema+bucket readiness, process-only liveness, durable cleanup/recovery | PASS / PASS |
| Технологии/процесс | exact versions/digest gate; нет startup migrations, нового auth или generic framework | PASS / PASS |

PASS относится к **проектному решению**. Runtime gates не выполнены этой
документационной командой и обязательны для implement.

## Project Structure

### Documentation (this feature)

```text
specs/002-files-avatars/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── references/source-constraints.md
├── checklists/requirements.md
└── contracts/
    ├── http-api.md
    ├── openapi.yaml
    └── operations.md
```

`tasks.md` создаёт следующая команда `$speckit-tasks`; сейчас не создаётся.

### Source Code (repository root; новые каталоги — проект реализации)

```text
src/BlogEngine.Core/
├── Accounts/Contracts/                  # текущие identity/roles
├── Media/{Domain,Application,Contracts}/ # новый модуль
├── Publishing/                         # atomic media registrations
└── ReadModels/                         # author/avatar/media contracts
src/BlogEngine.Infrastructure/
├── Media/                              # Garage, PNG, disk spool
├── Persistence/Media/                   # mappings, tx adapters
├── Persistence/Publishing/              # write scope и command snapshot
├── Persistence/Migrations/              # additive migration
├── ReadModels/                         # coherent batch projections
└── Maintenance/                        # durable processing/cleanup
src/BlogEngine.Api/{Contracts,Endpoints,Configuration}/
tests/BlogEngine.UnitTests/              # Media rules/architecture
tests/BlogEngine.IntegrationTests/{Fixtures,Media,Contracts,Operations}/
ops/{database,media}/                   # grants/provisioning
scripts/validation/                     # media/backup/restore checks
docs/{api,operations}/                  # transition и runbooks
compose.yaml                            # Garage dev profile
Directory.Packages.props                # pins и project lock files
```

**Structure Decision**: текущие четыре production-проекта сохраняются.
Media выделяется namespace так же, как остальные модули. Общий BlogDbContext
не разрешает бизнес-коду обходить контракты. Operator прав на Media не получает.

## Design: ownership, transactions and lifecycle

Граф Core: Media → Accounts.Contracts; Publishing → Accounts.Contracts +
Media.Contracts; ReadModels → существующие контракты + Media.Contracts.
Media.Contracts предоставляет операции usage внутри существующего publication
write scope. Publishing владеет доступом/версией/lifecycle статьи; Media —
проверкой owner/Ready/purpose и registrations. Реализации делят connection/tx,
не вызывают самостоятельный commit. Delete publication снимает registrations
в том же commit. Delete file проверяет собственные usages/current avatar,
без обратного вызова Publishing. Публичное чтение компонуется в ReadModels/API.

Порядок locks для всех writers: publication при наличии → allowance owner →
avatar state при наличии → files по UUID → uploads/jobs. Attach vs delete
защищены owner lock и FK; ссылки/текст — publication version. Никакая tx
не удерживается при Garage/network/image processing.

Init с Idempotency-Key сохраняет uploadId/fileId и резерв. PUT имеет fenced
lease и уникальный object attempt key. Complete сохраняет job; worker после
проверки bytes пишет производные, затем повторно проверяет expiry/current
roles/avatar version и атомарно делает файл Ready. Конфликт avatar терминален:
старая ссылка остаётся, новые объекты очищаются. Temporary failure допускает
retry той же попытки; terminal rejection требует новой. Deadline не продлевается.
Результат complete хранится как исторический результат; replay не назначает
avatar снова и не оживляет удалённый файл. Tombstones предотвращают повторную дельту.

Quota = ready original bytes + неистёкшие reservations. Перевод reserve→used
ровно один раз; старый avatar вычитается в commit замены. До swap резервируется
полный новый размер. Derived bytes не входят в user quota. Ready library
не имеет age-based TTL. Детали — [data-model.md](data-model.md).

После проверки owner точный init/Completed replay обрабатывается до guards
текущей роли/expiry/avatar version: эти guards применяются к новому переходу.

## Contract and compatibility decisions

[http-api.md](contracts/http-api.md) и [openapi.yaml](contracts/openapi.yaml)
задают новые endpoints/DTO. При реализации публикуется один полный aggregate:
все старые paths + delta + расширенные schemas. MVP YAML остаётся историческим
baseline. Все sections components объединяются; внешние refs к baseline
нормализуются во внутренние refs: опубликованный aggregate самодостаточен.
ApiRegistration/OpenApiContract переключаются на aggregate;
drift реального binding/serialization проверяется до overlay. Согласованный
transition всех поддерживаемых v1 consumers фиксируется в docs/api до activation:
strict old schema clients обновляются вместе с API. До готовности consumers
функция не активируется. Новый API major при таком переходе не требуется.

Legacy external CoverUrl/Markdown сохраняются. Отсутствующие media properties
при update сохраняют managed registrations; явные значения меняют их.
Presence-aware binding обязателен. Echo текущего managed coverUrl старым
клиентом не превращает его во внешний источник. AuthorRef/Me получают nullable
avatar; Hidden/Deleted author остаётся null. Batch reads не вызывают Identity
за именами/email. Binary endpoint metadata обходит JSON middleware buffer,
сохраняя прежний JSON limit 8 MiB; нельзя просто повысить глобальный предел.

## Review intersections, validation and rollout

| Замечание ревью 2026-10-27 | Обязательная работа в затронутой области |
|---|---|
| F01 | create/edit/transitions и новые commands возвращают snapshot своей tx, без re-read после commit; lost-response tests |
| F05 | feed/detail/workspace/comment author+media читаются одним snapshot; consistency/query count tests |
| F06 | aggregate OpenAPI + независимые HTTP/schema tests; deliberate drift проваливает проверку |
| F07 | readiness: Media schema/read grants + bounded Garage sentinel GET; без DDL/repair |
| F08 | binary route Accept/body policies, counted streaming вместо JSON buffering |

Остальные замечания сохраняются в исходном review. План не объявляет весь
backend исправленным. Матрица SC/команды — [quickstart.md](quickstart.md);
topology/options/backup — [operations.md](contracts/operations.md).

Rollout: pins/digest и реальные fixtures → additive migration/grants отдельным
DDL principal → Garage provisioning → compatible API/consumer deployment →
readiness/smoke → activation. После первого managed link старый backend
к записи не допускается. Rollback сохраняет additive schema и закрывает
mutations либо восстанавливает весь recovery set с учётом новых записей.
Production Down запрещён.

## Complexity Tracking

Нарушений конституции нет. Leases, durable jobs и owner lock требуются
FR-008/009/019/022/026–030. Отдельная очередь, distributed tx, generic UoW,
MediatR и CQRS framework не вводятся. Disk spool — цена целостности FR-030.
