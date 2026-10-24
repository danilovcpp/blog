# Implementation Plan: Универсальный Blog Engine — MVP

**Branch**: `main` (фактическая Git-ветка; новая ветка не создавалась) | **Date**: 2026-10-24 | **Spec**: [spec.md](spec.md)

**Feature context**: `001-blog-engine-mvp` — значение `BRANCH` setup-plan из имени каталога; это не Git-ветка.

**Status**: Ready for implementation — спецификация, планирование и генерация задач завершены 2026-10-24.
Реализация не начата; запуск — только по отдельной команде.

**Input**: `specs/001-blog-engine-mvp/spec.md`, согласованные Q1–Q3, [исходные ограничения](references/source-constraints.md), Identity 1.1.0, конституция 1.0.0.

## Summary

Создать повторно используемый backend одного проекта: Draft/Published, категории/теги,
дерево комментариев с последующей модерацией, идемпотентные лайки, просмотры с окном
24 часа и локальные роли поверх внешнего Bearer JWT. Модульный ASP.NET Core монолит
с независимым Core, EF Core/Npgsql и PostgreSQL. Блокировка материала и транзакции
обеспечивают согласованность жизненного цикла/взаимодействий; отдельная edit version
защищает правки. Границы функции задаёт FR-042.

## Technical Context

**Language/Version**: C# 14, `net10.0`, SDK 10.0.402, nullable и встроенные анализаторы.

**Primary Dependencies**: ASP.NET Core/JwtBearer/OpenAPI 10.0.13; EF Core/Design 10.0.13;
Npgsql EF provider/driver 10.0.3; Swagger UI 10.3.1 в Development. Pins/источники: [research.md](research.md), R-01/R-09.

**Storage**: PostgreSQL 18.6, UTC, collation `C`, без расширений; одна БД/DbContext,
EF migrations. БД Identity движку недоступна.

**Testing**: xunit.v3 4.0.2; VSTest adapter 4.0.1/Test SDK 18.11.0;
WebApplicationFactory 10.0.13, Testcontainers.PostgreSql 4.16.0, реальный PostgreSQL,
изолированные RSA/JWKS fixtures, управляемое время и барьеры.

**Target Platform**: Linux x64 server/container; Windows PowerShell/.NET CLI для разработки;
несколько реплик одной установки с общей БД и key ring.

**Project Type**: web-service и отдельный эксплуатационный CLI.

**Performance Goals**: SC-005: p95 <=1 s, p99 <=2 s, unexpected failures <=1%
при 100 параллельных читателях/10 min; bounded feed queries без N+1.
Стенд и базовые доли запросов зафиксированы в R-06. Нормативный `sc005-v1`
(seed 20261024, Markdown 70/25/5% по 2/16/128 KiB, 200 categories/2 000 tags,
точные частоты назначений и 87,5% непустых/12,5% пустых ответов) задан в
[профиле SC-005](spec.md#приёмочный-профиль-sc-005); алгоритм/квоты и порядок
измерений — в quickstart. SQL/query count/EXPLAIN проверяются также для
списков categories/tags из T043 на основном и увеличенном отдельном наборе.

**Constraints**: Markdown <=1 MiB UTF-8; JSON body <=8 MiB; pageSize <=100;
дерево <=5; 5 категорий/20 тегов; RS256, exact issuer/audience, skew <=30 s;
JWKS freshness <=300 s, timeout 5 s, fetch cooldown 30 s; runtime без DDL;
никаких внешних HTTP внутри DB-транзакций; секреты/токены вне Git/логов.

**Scale/Scope**: 7 историй, 42 FR, 4 QR, 8 SC; 100 000 опубликованных материалов,
1 000 000 комментариев, 1 000 000 лайков, накопленные 10 000 000 просмотров.

## Constitution Check

Gates проверены перед исследованием на выбранном подходе и повторно после Phase 1.
PASS означает соответствие проектного решения; тесты приложения ещё предстоят.

| Gate | До Phase 0 | После Phase 1: подтверждение |
|---|---|---|
| I. Модульность/независимость | PASS: чистый Core/порты | PASS: граф/владение ниже, Publishing scope, read contracts, четыре проекта с реальными границами |
| II. Завершённый C# | PASS: async/NRT, без лишних паттернов | PASS: build/format/analyzers в quickstart, единые правила, CancellationToken, без рабочих TODO |
| III. Данные | PASS: EF/Npgsql/migrations/constraints | PASS: data-model с FK/UNIQUE/CHECK/indexes, locks/version/atomicity, batch reads, миграционные проверки; T120 явно проверяет SQL/query count/проекции/EXPLAIN растущих categories/tags |
| IV. Контракты/безопасность | PASS: DTO/ProblemDetails/JWT/deny-by-default | PASS: OpenAPI/operations с ролями/404/вводом, недоверенным текстом, key cache, CORS/config |
| V. Проверки | PASS: unit + HTTP/real PG, fixtures | PASS: quickstart/матрица с negative/boundary/concurrent/migration; skip не успех |
| VI. Эксплуатация | PASS: health/logs/options/timeout, отдельные credentials | PASS: readiness/JWKS, CLI audit, shutdown, protected config, отдельное применение миграций |
| Технологии/процесс | PASS: поддерживаемый стек/MVP | PASS: pins R-01/R-09, одна major БД, покрытие FR ниже; tasks.md сформирован |

Нарушений/исключений из конституции и нерешённых бизнес-вопросов нет. Настройки
реального Identity — deployment inputs; автоматические тесты используют fixtures.

## Project Structure

### Documentation (this feature)

```text
specs/001-blog-engine-mvp/
├── spec.md
├── plan.md
├── tasks.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── openapi.yaml
│   └── operations.md
├── references/
└── checklists/
```

Генерация [tasks.md](tasks.md) завершена: сформированы 125 задач реализации и проверок.

### Source Code (repository root)

Проектируемая структура реализации; сейчас в репозитории только Spec Kit/документация.

```text
BlogEngine.slnx
global.json
Directory.Build.props
Directory.Packages.props
.config/dotnet-tools.json
compose.yaml
src/
├── BlogEngine.Core/
│   ├── Accounts/{Domain,Application,Contracts}/
│   ├── Publishing/{Domain,Application,Contracts}/
│   ├── Discussions/{Domain,Application,Contracts}/
│   ├── Engagement/{Domain,Application,Contracts}/
│   └── ReadModels/
├── BlogEngine.Infrastructure/
│   ├── Persistence/{Accounts,Publishing,Discussions,Engagement,Migrations}/
│   ├── ReadModels/
│   ├── Identity/
│   ├── Visitors/
│   └── Maintenance/
├── BlogEngine.Api/{Endpoints,Contracts,Configuration,Diagnostics}/
└── BlogEngine.Operator/
tests/
├── BlogEngine.UnitTests/
├── BlogEngine.IntegrationTests/
└── BlogEngine.Performance/
scripts/
├── validation/{Invoke-MigrationValidation,Invoke-QueryInspection}.ps1
└── performance/Invoke-ReadBenchmark.ps1
```

**Structure Decision**: Core объединяет Domain/Application в чистой assembly;
Infrastructure изолирует БД/внешние механизмы; Api/Operator разделяют hosts/privileges.
Unit suite проверяет архитектурные зависимости. Нет generic repository/UoW, MediatR,
assembly на каждый модуль, брокера или межсервисной коммуникации.

## Границы модулей и поток операций

| Модуль | Владеет | Разрешённые зависимости/контракты |
|---|---|---|
| Accounts | local users, elevated roles, role audit | Без продуктовых зависимостей; verified identity → immutable CurrentActor |
| Publishing | материалы, справочники/назначения, dates/version, физические counters | Accounts; publication write scope, state/ownership guard, bounded counter delta |
| Discussions | комментарии, видимость, ancestor counts | Accounts + Publishing scope |
| Engagement | likes, view dedup, visitor keys | Accounts + Publishing scope/counter contract; Publishing не зависит от Engagement |
| ReadModels | DTO, без собственных записей | Явные read contracts модулей; batch projections в Infrastructure |

Core не зависит от ASP.NET/EF/Npgsql/Identity SDK. Infrastructure → Core;
Api/Operator → Core + Infrastructure для composition. Module contracts используют
доменные значения/DTO/CancellationToken, не DbContext/IQueryable/HTTP.
Infrastructure объединяет mappings только в объявленных read/transaction contracts.
Handlers выполняют protocol mapping/validation; бизнес-инварианты имеют одно место.
JWT проверяется вне транзакции; local identity/roles загружаются в начале операции.
Publishing scope владеет transaction/lock, связанные записи используют тот же
connection/transaction без независимых commits. Lock order описан в data-model.

## Контракты, данные и жизненный цикл

Нормативные интерфейсы: [OpenAPI 3.1](contracts/openapi.yaml),
[security/operator supplement](contracts/operations.md); схема — [data-model.md](data-model.md).

- `X-Resource-Version` обязателен: stale =409, отсутствие/неверный header =400.
  Наличие закрытого ресурса и version не раскрываются до проверки доступа.
- GET не регистрирует просмотр; выделен POST. Анонимный ID сохраняет потребитель.
- Draft/deleted не проходят public parent guard. Первая публикация фиксирует timestamp/slug;
  правки Published видны сразу. Tombstone резервирует slug навсегда.
- Comments видны сразу; Hidden/Deleted с видимыми потомками образуют безопасные заглушки.
  Защищённый GET даёт владельцу version Hidden для удаления с content=null.
  Admin может править Hidden, сохраняя состояние; Deleted не восстанавливается.
- DTO запрещают неизвестные поля; ProblemDetails включает auth/challenge/forbid/413.
  Markdown — недоверенный source, comments — plain text, обложка не скачивается.
- Персонализируемые ответы: `private, no-store`, `Vary: Authorization`;
  непригодный переданный Bearer не становится anonymous. CORS по allowlist, без cookies;
  токены/секреты/visitor IDs исключены из диагностики.

## Phase 0 — результат исследования

[research.md](research.md) закрывает стек, архитектуру, concurrency, дерево/счётчики,
анонимный ID, JWKS, CLI и стенд. SDK/runtime доступны; Docker engine недоступен
текущей сессии по правам. Документацию это не блокирует; PG-тестам потребуется engine.

## Phase 1 — результат планирования и передачи в tasks

Созданы data-model, OpenAPI, operations и quickstart. Генерация `tasks.md` завершена;
задачи включают проверки вместе с кодом, миграциями и эксплуатационными файлами.

| Требования | Реализация/обязательные проверки |
|---|---|
| FR-001–011, SC-001 | lifecycle, slug/first publish, UTF-8/URL/лимиты, assignment/delete races, tombstone uniqueness, версии/rollback; unit + HTTP + DB |
| FR-010/012–017, QR-001, SC-005/006 | public/workspace/taxonomy, any/all filters, ties/pages, author refs; feed SQL <=5, taxonomy SQL =1, проекции/EXPLAIN/index evidence T120; read benchmark по sc005-v1 |
| FR-018–025, SC-002/006 | depth/cross-parent/cycles, корни/дети, hide/restore/delete/ancestor counts, версии, заглушки, parent state races |
| FR-026–031, SC-003/004 | 100 concurrent likes/views, mixed users, repeat remove, exact 24h, no sliding, auth/anonymous, cleanup race, multi-host общий DB/keyring, rollback |
| FR-032–038, SC-007 | role/action/ownership matrix, JWT/claims/isolation, JWKS rotation/outage/flood, bootstrap, next-operation roles, CLI/audit/DB privileges |
| FR-039–042, QR-003/004, SC-008 | OpenAPI consumer walkthrough, все категории ProblemDetails, unknown fields/PII/tokens, границы MVP |
| QR-002, конституция III/V/VI | build/format/analyzers, unit/real PG HTTP, migrations empty/upgrade/SQL, no app DDL, health/cancellation/timeouts/shutdown/config |

Первая схема проверяется с нуля. Upgrade — с реально существующей предыдущей
migration, когда она появится; до неё неприменимость отмечается с причиной.
Перед production изменением оцениваются locks, backup/restore, rollout;
применение отдельным шагом с DDL credentials. EnsureCreated не заменяет миграции.
Команды/сценарии: [quickstart.md](quickstart.md). Live Identity и performance targets
ещё не проверены; acceptance — результат будущей реализации.

## Complexity Tracking

Исключений из Constitution Check нет. Текущими инвариантами обоснованы publication
lock (R-03), ancestor counts (R-04), signed anonymous ID (R-05), JWKS adapter и
Operator host (R-07/R-08). Альтернативы рассмотрены в research.
