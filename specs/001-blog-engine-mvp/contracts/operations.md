# Эксплуатационный контракт и безопасность Blog Engine MVP

Нормативное дополнение к [OpenAPI](openapi.yaml), [модели данных](../data-model.md)
и решениям [R-03, R-05, R-07, R-08](../research.md).
Здесь MUST означает обязательное поведение реализации и приёмочных проверок.
Автоматические проверки реализации используют изолированные JWT/JWKS fixtures.
Фактические исходы и блокировки находятся в [implementation-report.md](../implementation-report.md);
реальный Identity и production rollout отдельно не проверены.

## 1. Граница личности и конфигурация

- Движок MUST принимать Bearer access JWT одного фиксированного Identity своего проекта.
  Он не реализует login/register/refresh/logout, BFF, cookie-сессии, выдачу токенов,
  OIDC Authority/discovery, introspection или синхронизацию профилей.
- Для API оператор MUST предоставить точные `Identity:Issuer`, `Identity:Audience`,
  `Identity:JwksUrl` отдельно для среды. Operator host требует только `Identity:Issuer`
  для проверки своей установки; audience/JWKS/visitor key ring этому host не нужны.
  Настройки соответствующего host валидируются при старте; примерные адреса и fallback
  на dev-конфигурацию запрещены.
  В production JWKS MUST использовать HTTPS; HTTP разрешён только явно локальному dev-стенду.
- Проверенная пара `(issuer, sub)` MUST разрешаться в один LocalUser, включая конкурентное
  первое обращение. Reader — обязательная неявная роль. Email, displayName, пароль,
  `sid`, access/refresh tokens не сохраняются в локальном пользователе.
- `GET /api/v1/me` MUST требовать Bearer, создавать недостающую локальную связь и возвращать
  `200` с `{ "id": "<local-user-uuid>", "roles": ["Reader"] }` либо текущими ролями.
  Порядок ролей: Reader, Author, Moderator, Admin; дубликатов нет.
  Публичный AuthorRef содержит только `{ "id": "<local-user-uuid>" }`;
  endpoints публичного профиля отсутствуют.
- Права MUST читаться из БД в начале каждой операции без кеша между запросами.
  Изменение роли действует для операции, начатой после commit оператора;
  уже выполняемая операция использует свой начальный authorization snapshot.
  Роли из JWT, JSON, query string и headers не дают локальных прав.

## 2. JWT, JWKS и отказы

JWT MUST проверяться штатным JwtBearer/IdentityModel: `MapInboundClaims=false`,
`SaveToken=false`, `IncludeErrorDetails=false`; RS256, корректная подпись и известный
`kid`, точные issuer/audience, `IgnoreTrailingSlashWhenValidatingAudience=false`,
`TryAllIssuerSigningKeys=false`, clock skew 30 секунд. Обязательны однозначные scalar
`sub`/`sid` UUID, непустые `client_id`/`jti`, `token_use=access`, числовые `iat`/`nbf`/`exp`.
Дубликаты, неправильные типы, будущий `iat` вне skew и неверные временные границы
отклоняются. `alg=none`, подмена алгоритма и непригодные ключи запрещены.
`typ=JWT` допустим; выдуманный `at+jwt` не требуется. Срок JWT не фиксируется на 900 s.
Максимум предъявленного JWT — 16 KiB в его encoded UTF-8 представлении.

JWKS loader MUST использовать только `JwksUrl`: redirect запрещён, `jku`/`x5u`
не выбирают источник. Timeout всей загрузки — 5 s; прочитанное тело после декодирования
не более 256 KiB, массив `keys` — не более 32. Принимаются только пригодные публичные
RSA signing keys с `alg=RS256` и однозначным `kid`; двусмысленный/некорректный набор
не заменяет рабочий snapshot. Приватные ключи Identity движку не нужны.

Свежесть snapshot MUST быть не более 300 s; меньший server `max-age` соблюдается.
Параллельные fetch MUST объединяться; между началами попыток одного процесса —
не менее 30 s, включая неудачные попытки. Неизвестный `kid` разрешает максимум одно
ограниченное обновление и одну повторную проверку в запросе. Snapshot заменяется атомарно;
удалённый при обновлении ключ и snapshot после истечения срока не используются.
Last-known-good не продлевает этот срок. Fresh matching key проверяется без сети.

| Состояние | HTTP и стабильный `code` |
| --- | --- |
| Bearer отсутствует у защищённой операции | `401 authentication_required` |
| Bearer непригоден: формат, claims, подпись, время, размер | `401 invalid_token` |
| Успешно загружен JWKS, `kid` отсутствует | `401 invalid_token` |
| `kid` неизвестен, есть fresh snapshot, refresh блокирует cooldown | `401 invalid_token` |
| Нужен fetch, пригодного ключа нет, upstream/network/format отказ | `503 identity_keys_unavailable` |
| Нужен fetch после прежнего отказа, пригодного snapshot нет, действует cooldown | `503 identity_keys_unavailable` |
| Личность проверена, локального права/владения недостаточно | `403 forbidden` |

Ответы об ошибках MUST использовать `application/problem+json`, `code` и `traceId`,
без токена, ключей, адреса upstream, exception/SQL деталей. `401` содержит
`WWW-Authenticate: Bearer`; HTML redirect на вход отсутствует. Отказ сети не отключает
проверку подписи. Детали причины доступны только в безопасной структурированной диагностике.
Отзыв сессии/деактивация Identity может обнаруживаться до `exp + 30 s`;
повторная проверка `/users/me` и мгновенный глобальный logout не обещаются.

Общие HTTP-ошибки имеют следующие стабильные `code`; детали authentication/JWKS
различаются по таблице выше:

| HTTP | `code` |
| --- | --- |
| `400` | `validation_failed`, `invalid_request` |
| `401` | `authentication_required`, `invalid_token` |
| `403` | `forbidden` |
| `404` | `resource_not_found` |
| `405` | `method_not_allowed` |
| `406` | `not_acceptable` |
| `409` | `concurrency_conflict`, `slug_conflict`, `taxonomy_in_use`, `invalid_state` |
| `413` | `request_too_large` |
| `415` | `unsupported_media_type` |
| `500` | `internal_error` |
| `503` | `dependency_unavailable` для БД, `identity_keys_unavailable` для обязательного JWKS |

## 3. Чтение, видимость и версии

- Публичные content endpoints принимают optional Bearer для персонализации. Если Bearer
  предъявлен, он MUST проверяться; непригодный Bearer даёт `401`, невозможность загрузить
  необходимые ключи — `503`. Молчаливого перехода в анонимный режим нет.
- Ответы публичного контента с optional Bearer MUST содержать
  `Cache-Control: private, no-store` и `Vary: Authorization`, в том числе анонимные ответы.
  Защищённые ответы, включая `/me` и workspace, MUST содержать `Cache-Control: no-store`.
- После необходимой проверки личности публичное чтение, обсуждение, лайки и просмотры
  требуют доступного Published-материала. Закрытый/удалённый/несуществующий материал
  возвращает одинаковый `404 resource_not_found`, без проверки, раскрывающей дочерние данные.
  Workspace управляет доступными автору/Admin материалами по отдельным маршрутам OpenAPI.
  Для доступного ресурса отсутствие требуемой роли/владения даёт `403 forbidden`.
- Защищённый `GET /api/v1/comments/{commentId}` — контекст редактирования владельца/Admin
  при Published-материале. Для своего Hidden-комментария владелец получает `content=null`
  и `version`, что позволяет удаление; Admin видит скрытый текст. Moderator использует
  список модерации. Публичная заглушка не раскрывает текст и автора.
- Mutable publication/comment/taxonomy DTO содержит `version` UUID. Для изменения,
  удаления и перехода состояния клиент MUST передать `X-Resource-Version: <UUID>`.
  Отсутствие/формат — `400 validation_failed`; несовпадение — `409 concurrency_conflict`.
  Успешная содержательная запись выдаёт новую version. Служебные счётчики её не меняют.
  Это версия редактирования; `ETag`/`If-Match` не используются.

## 4. Регистрация просмотра и анонимный идентификатор

`POST /api/v1/publications/{id}/views` MUST быть отдельной bodyless-операцией;
чтение ленты/материала/счётчиков не регистрирует просмотр. Optional Bearer имеет
приоритет; для анонимного посетителя потребитель передаёт `X-Anonymous-Visitor`.
Успешный `200` возвращает:

```json
{ "counted": true, "viewCount": 1, "anonymousVisitorId": "<opaque-token-or-null>" }
```

Для проверенного пользователя ключ — local user ID, `anonymousVisitorId=null`,
анонимный header не влияет на учёт. Для анонимного запроса движок возвращает пригодный
предъявленный token либо выдаёт новый, если token отсутствует/непригоден.
Значение `X-Anonymous-Visitor` длиннее 256 символов считается непригодным и заменяется
новым token; общие лимиты HTTP header envelope сервера действуют независимо.
Потребитель MUST сохранить непрозрачное значение и повторно передавать его;
потеря token означает нового анонимного посетителя. Cookie движок не выставляет.

Формат token — `v1.<keyId>.<random256>.<mac>`; random256 — 32 криптографически случайных
байта, mac — HMAC-SHA256 над UTF-8 `v1.<keyId>.<random256>`, оба в base64url без padding.
MAC сравнивается за постоянное время. Отдельный защищённый key ring установки
MUST быть одинаков у всех реплик; ключ подписи Identity для этого не используется.
Конфигурация `AnonymousVisitors:Keys` — map `keyId` → base64-секрет ровно из 32 случайных
байтов; `AnonymousVisitors:ActiveKeyId` MUST указывать на существующий элемент map.
Каждый настроенный `keyId` MUST соответствовать `^[A-Za-z0-9_-]{1,32}$`;
точки запрещены. Пустой map, неверный ID любого ключа, отсутствующий активный ключ
и некорректный base64/размер секрета запрещают старт. Длина выпущенного token равна
`91 + length(keyId)`, максимум 123 символа, в пределах контрактного лимита 256.
Текущий ключ выпускает token, сохранённые старые ключи проверяют его до вывода из эксплуатации.
Удаление ключа делает соответствующие token непригодными; порядок ротации документируется оператором.
В dedup хранят SHA-256 типизированного стабильного visitor ID, без исходного token.
Токены, анонимные ID и request body не попадают в логи.

Повтор до `last_counted_at + 24 h` возвращает `counted=false`, не сдвигает окно
и не увеличивает счётчик. В точности на границе допускается один новый просмотр.
После входа анонимная история не объединяется. Закрытый материал даёт `404`
без token/изменения счётчиков. Эти ответы MUST содержать `Cache-Control: no-store`.

## 5. Операторский CLI

`BlogEngine.Operator` — отдельный console host, доступный только защищённой операторской
среде. HTTP runtime MUST NOT иметь средств записи повышенных ролей; пользователь Admin
не получает операторский доступ. CLI не запускает HTTP-сервер и не обращается к Identity.

```text
BlogEngine.Operator users find --issuer <issuer> --subject <uuid>
BlogEngine.Operator roles list --user-id <local-uuid>
BlogEngine.Operator roles grant --user-id <local-uuid> --role <Author|Moderator|Admin> --reason <text>
BlogEngine.Operator roles revoke --user-id <local-uuid> --role <Author|Moderator|Admin> --reason <text>
```

`--issuer` MUST в точности совпадать с настроенным issuer; он не меняет доверенный
экземпляр. UUID разбирается до запроса к БД; неизвестные команды/параметры и роли
отклоняются. `--reason` обязателен, после trim содержит 1–512 символов.
CLI работает только с существующим LocalUser, без создания/изменения аккаунта Identity.
Reader нельзя назначить/отозвать; Author и Moderator совместимы. Первого Admin
назначает тот же `roles grant`. Нет автоматического Admin или правила последнего Admin.

stdout MUST содержать один JSON-объект успешного результата; роли упорядочены как в `/me`:

| Команда | Поля результата |
| --- | --- |
| `users find` | `userId`, `issuer`, `subject`, `roles` |
| `roles list` | `userId`, `roles` |
| `roles grant/revoke` | `userId`, `roles`, `changed`, `auditId`, `correlationId` |

Повторный grant уже назначенной роли и revoke отсутствующей роли — успешный no-op
с `changed=false`. Запись назначения и RoleChangeAudit MUST фиксироваться атомарно;
параллельные команды одного пользователя сериализуются transaction-scoped advisory lock
по local user ID. Коллизия ключа может только дополнительно сериализовать разных пользователей.
Audit содержит UUID, UTC timestamp, execution identity процесса/защищённой среды,
target user ID, роль, действие, reason, changed и correlationId. `--actor` не принимается.
No-op тоже оставляет audit. Ошибка audit откатывает назначение. Записи audit неизменяемы;
ошибочные/отменённые команды дают безопасное диагностическое событие без токенов/секретов.

| Exit code | Значение |
| --- | --- |
| `0` | Успех, включая idempotent no-op |
| `2` | Неверные аргументы/значения; изменений нет |
| `3` | Локальный пользователь не найден; изменений нет |
| `4` | Сбой БД/permissions/audit; rollback либо явно `outcome_unknown` при потере commit-ответа |
| `5` | Отсутствующая/непригодная конфигурация |
| `130` | Подтверждённая отмена до commit; изменений нет |

При ошибке stdout пуст; stderr содержит один JSON `{code,message,correlationId}`
без stack trace/connection string. После успешного commit отмена не выдаётся за rollback:
CLI сообщает успешный результат. При потере связи во время commit результат может быть
неизвестен: exit `4`, `code=outcome_unknown`, оператор проверяет audit по correlationId;
идемпотентный повтор не создаёт второе назначение.

## 6. Привилегии, health и эксплуатационные проверки

- Runtime DB role: нужный DML бизнес-таблиц, создание/чтение LocalUser,
  только SELECT RoleAssignment; без записи RoleChangeAudit и без DDL.
  Operator DB role: SELECT LocalUser/RoleAssignment, transaction advisory lock,
  INSERT/DELETE RoleAssignment, INSERT RoleChangeAudit и чтение audit
  для проверки outcome; без изменения бизнес-контента, UPDATE/DELETE audit и DDL.
  Credentials этих hosts MUST передаваться отдельно защищённой конфигурацией.
- EF migrations MUST запускаться отдельным deployment-шагом с отдельными DDL credentials,
  не автоматически при старте production. CLI не применяет миграции.
- `/health/live`: `200` живого процесса, без проверки внешних сервисов.
  `/health/ready`: `200` только при доступном PostgreSQL и fresh usable JWKS snapshot;
  иначе `503`. Fresh cache допускает готовность при outage Identity.
  Probe использует общий loader/cooldown и bounded refresh при отсутствии/истечении cache,
  что позволяет восстановление; HTTP fetch на каждый probe запрещён.
  Публичное тело health содержит только безопасный общий статус.
- Автотесты MUST проверять настоящий JWT HTTP-конвейер с одноразовыми RSA/JWKS fixtures,
  ротацию/cooldown/expiry/503, запреты прав и next-operation role changes, гонку bootstrap,
  CLI exit codes, atomic audit и разные DB permissions на PostgreSQL production major.
  Они не зависят от живого Identity. Реальное подключение требует отдельно предоставленной
  конфигурации и проверенного test-экземпляра; его успешность здесь не заявляется.
