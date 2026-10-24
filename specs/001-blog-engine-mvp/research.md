# Research: Blog Engine MVP

Дата решений: 2026-10-24. Основание: [spec.md](spec.md),
[исходные ограничения](references/source-constraints.md),
[конституция 1.0.0](../../.specify/memory/constitution.md).
Это проектирование; сборка, функциональные и нагрузочные проверки приложения ещё не выполнялись.

## R-01. Поддерживаемый стек и воспроизводимость

- **Decision**: .NET SDK 10.0.402, `net10.0`, C# 14; ASP.NET Core,
  `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.AspNetCore.OpenApi`,
  `Microsoft.AspNetCore.Mvc.Testing`, EF Core/Design и локальный `dotnet-ef` 10.0.13;
  `Npgsql.EntityFrameworkCore.PostgreSQL` и Npgsql 10.0.3; PostgreSQL 18.6.
  OpenAPI 3.1 генерируется штатным ASP.NET Core; Swagger UI 10.3.1 показывает его в Development.
- **Rationale**: .NET 10 — поддерживаемый LTS; версии подтверждены первичными источниками.
  Provider Npgsql 10.0.3 допускает EF Core от 10.0.4 до 11.0.0, поэтому 10.0.13 совместим.
  SDK закрепляется в `global.json` без prerelease, packages — в
  `Directory.Packages.props` и lock-файлах, инструменты — в `.config/dotnet-tools.json`.
  Production/test используют PostgreSQL major 18 без дополнительных расширений,
  UTC и одинаковую collation `C`; image `postgres:18.6` фиксируется также реальным digest
  при реализации, без выдуманного digest на этапе плана.
- **Alternatives considered**: .NET 8/9 имеют короткий оставшийся срок поддержки;
  .NET 11 prerelease не подходит; плавающие версии нарушают воспроизводимость.
- **Sources**: [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0),
  [support](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core),
  [EF Core](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore/10.0.13),
  [Npgsql provider](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3),
  [PostgreSQL versions](https://www.postgresql.org/support/versioning/),
  [OpenAPI](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/openapi?view=aspnetcore-10.0).

## R-02. Модули и зависимость от инфраструктуры

- **Decision**: модульный монолит, один API-процесс и PostgreSQL, четыре production-проекта:
  `BlogEngine.Core`, `BlogEngine.Infrastructure`, `BlogEngine.Api`, `BlogEngine.Operator`.
  В Core — framework-independent Domain/Application по модулям Accounts, Publishing
  (включая справочники), Discussions, Engagement, а также координирующие ReadModels.
  Infrastructure реализует порты хранения/времени/криптографии, Api и Operator — точки сборки.
- **Rationale**: отдельные hosts нужны для разделения HTTP и операторских привилегий;
  отдельная Core assembly проверяемо исключает зависимость бизнес-правил от EF/ASP.NET.
  Один DbContext и одна линия миграций позволяют атомарные сценарии без распределённых транзакций.
  Publishing предоставляет контракт блокировки доступного материала и изменения счётчиков;
  Discussions/Engagement используют его. ReadModels использует явно разрешённые контракты
  чтения модулей и DTO-проекции; общий DbContext не разрешает обход модульных границ.
- **Alternatives considered**: микросервисы/брокер, MediatR, generic repository/UoW,
  assembly на каждый модуль и дополнительный слой Application project не решают текущую задачу.

## R-03. Версии и единый порядок конкурентной записи

- **Decision**: непрозрачная UUID `version` у материала, комментария и справочника;
  `X-Resource-Version` обязателен для изменения, удаления и переходов состояния.
  Устаревшая версия — `409 concurrency_conflict`; отсутствие/формат — `400`.
  Каждая успешная содержательная запись меняет version; служебные счётчики — нет.
  Все записи, связанные с материалом, выполняются в короткой READ COMMITTED транзакции
  после `SELECT ... FOR UPDATE` строки материала, затем повторно проверяют публичность,
  владение, версию и состояние родителя. Порядок: материал → связанные записи;
  для нескольких ID — по возрастанию. Внешнего HTTP внутри транзакции нет.
- **Rationale**: блокировка PostgreSQL работает между репликами и устраняет гонки
  закрытия/удаления с ответом, лайком и просмотром. Обычные MVCC-чтения не блокируются.
  Edit version не должна инвалидироваться каждым просмотром.
- **Alternatives considered**: `If-Match`/strong ETag с edit-only версией неверно описывал бы
  представление с меняющимися счётчиками; PostgreSQL `xmin` связывает контракт с provider;
  optimistic concurrency без общей блокировки не защищает межтабличные инварианты.
  Цена решения — сериализация записей одного горячего материала; её измеряют отдельно,
  не обещая неоговорённую write-throughput цель.
- **Sources**: [row locks](https://www.postgresql.org/docs/18/explicit-locking.html),
  [EF concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

## R-04. Удаление, классификация, комментарии

- **Decision**: материал имеет терминальный `deleted_at`; UNIQUE slug включает tombstones.
  При удалении атомарно снимаются до 25 назначений категорий/тегов, чтобы удалённый
  материал не удерживал справочники навсегда. Скрытые дочерние записи остаются недоступны
  через общий parent visibility guard. Восстановления нет. Таксономии удаляются физически,
  если ссылок нет; FK RESTRICT разрешает гонку назначения/удаления без сирот.
  Комментарий неизменно привязан к материалу/родителю; composite FK проверяет тот же материал.
  Глубина 1–5; у вставляемого узла может быть только уже существующий видимый родитель.
  Deleted навсегда очищает текст; Hidden сохраняет текст только для модерации.
- **Rationale**: tombstone сохраняет slug без отдельного реестра адресов и массового удаления
  большого обсуждения. Для эффективных заглушек `visible_descendant_count` хранит число
  всех Visible-потомков: изменение видимости обновляет максимум четыре предка в той же
  транзакции. Публичный узел включается при Visible или count > 0; заглушка не содержит
  текста/автора. `hasReplies` проверяет включаемых прямых детей.
- **Alternatives considered**: hard delete плюс отдельный реестр slug сложнее;
  загрузка всего дерева/неограниченный recursive subtree scan нарушают ограниченность чтения;
  closure table не требуется при глубине пять.

## R-05. Лайки, просмотры и анонимная личность

- **Decision**: UNIQUE `(publication_id, user_id)` у лайка; INSERT ON CONFLICT DO NOTHING
  и DELETE RETURNING определяют изменение счётчика. `like_count`/`view_count` — bigint
  в строке материала с CHECK >= 0; менять их можно только через контракт Publishing
  в транзакции Engagement. Это даёт индекс популярности без дублирования состояния/дат.
  View dedup имеет PK `(publication_id, visitor_kind, visitor_key)` и `last_counted_at`.
  Условный UPSERT возвращает строку только для новой пары или
  `last_counted_at <= T - interval '24 hours'`; только тогда увеличивается view_count.
  Повтор не меняет timestamp. T берётся один раз из DB `clock_timestamp()` после блокировки.
- **Rationale**: ограничения и атомарные транзакции покрывают повторы/гонки/rollback;
  агрегат просмотров сохраняется после очистки dedup. Изолированный clock port позволяет
  детерминированные тесты без ожидания суток. Очистка — периодический HostedService:
  ограниченные партии по 500, каждые 15 минут, тот же порядок блокировок и повторная
  проверка возраста после блокировки; счётчик не уменьшается.
  Несколько workers безопасны; время запуска не влияет на правило дедупликации.
- **Decision**: анонимный token `v1.<keyId>.<random256>.<mac>` выдаётся только движком;
  HMAC-SHA256 и cryptographic RNG, отдельный key ring установки в защищённой конфигурации
  у всех реплик. В dedup хранится SHA-256 от типизированного стабильного идентификатора,
  а не сам token. Без/с непригодным token выдаётся новый. Для Bearer применяется локальный
  user ID, анонимная история не объединяется; cookies не используются.
- **Alternatives considered**: IP/device fingerprint нарушает ожидаемую стабильность;
  клиентский User ID не доказывает личность; один in-memory cache не работает между репликами;
  календарные сутки не равны окну 24 часа; event table всех просмотров не нужен.
- **Sources**: [UPSERT](https://www.postgresql.org/docs/18/sql-insert.html),
  [DB time](https://www.postgresql.org/docs/18/functions-datetime.html).

## R-06. Ограниченное чтение и приёмочный стенд

- **Decision**: номерная offset-пагинация по spec, page=1/pageSize=20, максимум 100;
  `pageSize + 1` определяет hasNextPage без обязательного COUNT.
  Проверяемый int64 offset; допустимые страницы за набором пусты. Назначения/фильтры
  категорий/тегов ограничены 5/20, повторные ID и неизвестные ID — 400.
  Feed: один ограниченный запрос карточек с correlated EXISTS лайка текущего пользователя,
  затем два batch-запроса назначений для ID страницы; валидация справочников — до двух
  дополнительных batch-запросов. Максимум пять data queries независимо от размера страницы,
  кроме фиксированных auth/bootstrap запросов, измеряемых отдельно.
  Индексы feed: `(first_published_at DESC,id DESC)` и
  `(like_count DESC,first_published_at DESC,id DESC)` с предикатом публичности;
  связи имеют индексы в обоих направлениях. Нет lazy loading.
- **Rationale**: это удовлетворяет контракту страниц без N+1. Keyset pagination несовместима
  с требуемым номером страницы; snapshot не обещается. Проверяются SQL и EXPLAIN,
  особенно глубокие страницы и комбинации фильтров.
- **Decision**: стенд SC-005: Linux x64, API 4 выделенных vCPU/8 GiB, PostgreSQL
  8 vCPU/16 GiB/NVMe SSD, генератор 2 vCPU/4 GiB; сеть >=1 Gbit/s, одна API-реплика,
  без посторонней нагрузки. 100 closed-loop VU, 2 минуты прогрева + 10 минут измерений;
  50% feed/50% detail, обе сортировки и размеры 20/100 поровну, 50% feed с фильтрами,
  10% всех запросов с Bearer. Начальные page 1/10/100/1000 выбираются равномерно.
  Данные/seed/железо/образы/SQL-планы фиксируются в отчёте. Порог p95 <=1 s,
  p99 <=2 s, unexpected failure <=1%, как SC-005. Отдельные сценарии проверяют каждый
  sort/page-size/filter вариант, чтобы общий percentile не скрывал медленный вариант.
  Многорепличная корректность проверяется отдельными конкурентными тестами.
- **Alternatives considered**: запрос связей для каждой карточки, total count на каждой
  странице и выдача Markdown в feed дают лишнюю стоимость; Redis/read replicas пока не нужны.
- **Sources**: [EF efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying),
  [pagination](https://learn.microsoft.com/en-us/ef/core/querying/pagination),
  [EXPLAIN](https://www.postgresql.org/docs/18/sql-explain.html).

## R-07. Bearer и жизненный цикл доверенных ключей

- **Decision**: только JwtBearer с `MapInboundClaims=false`, `SaveToken=false`,
  `IncludeErrorDetails=false`; RS256, точные issuer/audience, clock skew 30 s,
  `IgnoreTrailingSlashWhenValidatingAudience=false`, `TryAllIssuerSigningKeys=false`.
  Обязательны однозначные `sub`/`sid` UUID, `kid`, `client_id`, `jti`,
  `token_use=access`, числовые `iat`/`nbf`/`exp`; проверяются время и подпись.
  Дубликаты обязательных scalar claims/неправильные типы отклоняются. Не требуем
  выдуманного `at+jwt`; контракт выдаёт `typ=JWT`. Не фиксируем срок JWT равным 900 s.
- **Decision**: async JWKS adapter использует только фиксированный URL, HTTPS в production,
  timeout 5 s, freshness максимум 300 s (меньший server max-age соблюдается),
  single-flight и минимум 30 s между fetch attempts на процесс, включая неудачные.
  Ограничения: JWT 16 KiB, JWKS 256 KiB/32 ключа; только публичные RSA signing keys,
  `alg=RS256`, однозначный `kid`. Redirect запрещён; `jku`/`x5u` не выбирают источник.
  После истечения snapshot и при удалении ключа старый ключ не используется.
  Fresh matching key не требует сети; unknown kid допускает одно обновление/повторную
  проверку. Успешный fetch без kid или cooldown с fresh snapshot — 401;
  необходимый fetch без пригодного ключа и сетевой сбой — 503. Неверная подпись — 401.
- **Rationale**: штатный crypto verifier сохраняет стандартные проверки; небольшой async
  adapter задаёт требуемый cache policy без OIDC Authority. Обычный RequestRefresh не
  гарантирует retry в том же запросе; реализация должна явно обеспечить один async retry,
  не блокировать sync key resolver и не менять общие validation options на запрос.
  JWT revocation задержана до exp+30 s; повторная проверка `/users/me` исключена.
- **Alternatives considered**: OIDC/discovery отсутствует; постоянный публичный ключ
  не поддерживает ротацию; last-known-good после 300 s ослабляет принятое ограничение.
- **Sources**: [приложенный контракт](references/Identity_Integration_Spec.md),
  [TokenValidationParameters](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.tokens.tokenvalidationparameters?view=msal-web-dotnet-latest),
  [JwtBearerHandler source](https://source.dot.net/Microsoft.AspNetCore.Authentication.JwtBearer/JwtBearerHandler.cs.html).

## R-08. Локальные права и эксплуатация

- **Decision**: LocalUser UUID + UNIQUE `(issuer,subject)`; bootstrap через upsert/read
  только после JWT verification, в том числе `GET /api/v1/me` и персонализация.
  Reader — обязательная неявная базовая роль; таблица назначений хранит только
  Author/Moderator/Admin. Права читаются из БД в начале каждой операции без role cache.
  Отдельный Operator host ищет существующего пользователя и grant/revoke локальную роль
  с обязательной причиной и аудитом, не обращаясь к Identity.
- **Rationale**: HTTP DB credential имеет только SELECT на повышенные роли;
  отдельный operator credential — ограниченную запись назначений, без DDL и Identity.
  Нет HTTP-управления ролями и автоматического Admin; первый Admin назначается тем же CLI.
  Revocation роли действует для операции, начатой после commit; уже выполняемая операция
  использует начальный authorization snapshot.
- **Decision**: liveness — процесс; readiness — PostgreSQL и fresh JWKS snapshot.
  Fresh snapshot позволяет обслуживать запросы при outage Identity; probe использует
  общий loader/cooldown, может восстановиться через bounded fetch после истечения cache.
  Production migration запускается отдельным шагом с отдельными DDL credentials.
  Логи структурированы, без токенов/body/анонимных ID; конфигурация проверяется при старте.
- **Alternatives considered**: роли из JWT, кеширование ролей на срок JWT, публичное
  администрирование и production startup migrations противоречат требованиям.

## R-09. Проверки и граница текущего результата

- **Decision**: xunit.v3 4.0.2 + xunit.runner.visualstudio 4.0.1 +
  Microsoft.NET.Test.Sdk 18.11.0; VSTest выбран явно, команды `dotnet test <project>`.
  WebApplicationFactory и Testcontainers.PostgreSql 4.16.0 с настоящим PostgreSQL 18.6.
  Отдельные тесты CLI запускают Operator и проверяют БД/аудит/exit codes.
  Изолированные RSA/JWKS fixtures проверяют настоящий JWT HTTP middleware; clock seam,
  барьеры и отдельные connections контролируют гонки. Каждый изменяемый инвариант
  имеет unit/HTTP/persistence проверки на подходящей границе.
- **Rationale**: EF InMemory/SQLite не подтверждают PostgreSQL-инварианты; live Identity
  не должен требоваться автоматическим тестам. Миграции проверяются с нуля, upgrade
  предыдущей схемы и по SQL; нет утверждения, что тесты уже прошли.
- **Alternatives considered**: смешивание MTP/VSTest CLI, заглушка всей аутентификации,
  проверки только happy path или только процента покрытия недостаточны.
- **Sources**: [xUnit release](https://xunit.net/releases/v3/4.0.2),
  [runner selection](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test),
  [ASP.NET integration tests](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0),
  [EF test strategy](https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy),
  [Testcontainers](https://dotnet.testcontainers.org/modules/postgres/).

## Закрытие неизвестных

Технологические неизвестные разрешены решениями R-01–R-09. Точные production issuer,
audience, JWKS URL, connection strings и key ring — обязательные параметры оператора,
не нерешённые проектные вопросы и не значения из примеров.
Локально подтверждены SDK 10.0.402 и ASP.NET Core runtime 10.0.13.
Docker CLI доступен, но доступ к engine `desktop-linux` из этой сессии возвращает
permission denied; это будущая предпосылка интеграционных тестов, не блокировка
создания документации. Реальная интеграция с Identity не проверялась.
