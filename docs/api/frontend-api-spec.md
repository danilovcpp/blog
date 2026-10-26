# Blog Engine: спецификация API для проектирования frontend

Версия API: **1.0.0**, префикс **`/api/v1`**, OpenAPI **3.1.0**.
Документ подготовлен **27 октября 2026 года** по текущим файлам рабочего дерева.
Адрес конкретной установки не задан. Все UUID, даты, адреса и токены в примерах
иллюстративные; использовать их как конфигурацию среды нельзя.

Это подробное описание существующего backend для агента, проектирующего
публичный сайт, кабинет автора и интерфейс администрирования. Здесь зафиксированы
HTTP-контракты, модели, ограничения, права, переходы состояний и последствия для UI.
Рекомендации по устройству frontend помечены отдельно от поведения сервера.

Нормативные источники:

- [OpenAPI — все 35 операций и JSON Schema](../../specs/001-blog-engine-mvp/contracts/openapi.yaml).
- [Дополнение: безопасность, видимость, версии и эксплуатация](../../specs/001-blog-engine-mvp/contracts/operations.md).
- [Функциональная спецификация](../../specs/001-blog-engine-mvp/spec.md).
- [Реализация HTTP endpoints](../../src/BlogEngine.Api/Endpoints).
- [Готовые TypeScript DTO для передачи frontend-агенту](frontend-api-types.ts).

OpenAPI определяет форму обмена. Этот документ раскрывает её для проектирования
интерфейса и уточняет поведение по реализации. При будущем расхождении источников
нужно проверить изменение контракта, а не придумывать совместимость на клиенте.
Документ не вводит новые серверные функции.

## Содержание

1. [Возможности и границы](#1-возможности-и-границы).
2. [Подключение и HTTP](#2-подключение-и-http).
3. [Личность и права](#3-личность-и-права).
4. [Общие типы и валидация](#4-общие-типы-и-валидация).
5. [Пагинация, фильтры и сортировка](#5-пагинация-фильтры-и-сортировка).
6. [Модели данных](#6-модели-данных).
7. [Каталог операций](#7-каталог-операций).
8. [Текущий пользователь](#8-текущий-пользователь).
9. [Публичные публикации](#9-публичные-публикации).
10. [Workspace и управление публикациями](#10-workspace-и-управление-публикациями).
11. [Категории и теги](#11-категории-и-теги).
12. [Комментарии](#12-комментарии).
13. [Модерация](#13-модерация).
14. [Лайки и счётчики](#14-лайки-и-счётчики).
15. [Просмотры](#15-просмотры).
16. [Health и служебная документация](#16-health-и-служебная-документация).
17. [Ошибки](#17-ошибки).
18. [Версии, повтор запросов и гонки](#18-версии-повтор-запросов-и-гонки).
19. [Требования к frontend-интеграции](#19-требования-к-frontend-интеграции).
20. [Пользовательские сценарии](#20-пользовательские-сценарии).
21. [Контрольный список приёмки](#21-контрольный-список-приёмки).
22. [Настройки и вопросы для интеграции](#22-настройки-и-вопросы-для-интеграции).
23. [Приложение: граница внешнего Identity](#23-приложение-граница-внешнего-identity).
24. [Приложение: точные ответы всех операций](#24-приложение-точные-ответы-всех-операций).

## 1. Возможности и границы

Одна установка Blog Engine обслуживает один проект. Tenant ID, site ID и выбор
проекта в HTTP-контракте отсутствуют.

API предоставляет:

- Публичную ленту Published-материалов, фильтры категорий/тегов и сортировку по новизне/лайкам.
- Чтение публикации по UUID и постоянному slug.
- Рабочую область автора с Draft и Published, создание, редактирование,
  публикацию, снятие с публикации и окончательное удаление.
- Два плоских справочника: категории и теги; их изменяет Admin.
- Древовидные комментарии с отдельной пагинацией корней и ответов, глубина до 5.
- Редактирование и удаление собственных комментариев, последующую модерацию.
- Лайк текущего пользователя, счётчики и явную регистрацию открытия материала.
- Локальные роли текущего пользователя и health endpoints.

В API Blog отсутствуют:

- Регистрация, login, refresh, logout, восстановление пароля, управление сессиями и аккаунтами.
- Публичные профили, имена, аватары, email, биографии и список пользователей.
- HTTP-выдача/отзыв ролей, включая для Admin.
- Загрузка изображений/файлов, библиотека медиа и проверка доступности обложки.
- Поиск по тексту, фильтры автора/даты, произвольная сортировка и поиск по справочникам.
- Уведомления, подписки, WebSocket/SSE, история лайков и список поставивших лайк.
- Отложенная публикация, история редакций, отдельный черновик правок Published,
  восстановление удалённых публикаций и комментариев.
- Полное дерево комментариев одним запросом, общее число страниц и total count.

Следствие для проектирования: интерфейс может включать эти функции только после
согласования отдельного источника данных или расширения backend. Нельзя рисовать
работающий поиск, загрузку обложки или управление ролями как уже поддержанные функции.

## 2. Подключение и HTTP

### 2.1. Базовый адрес

`BLOG_API_ORIGIN` — origin установки; API-пути в документе уже содержат `/api/v1`.
Например, итоговый URL строится как `${BLOG_API_ORIGIN}/api/v1/publications`.
При same-origin развертывании допустимы относительные пути.
Если ingress добавляет path base, оператор должен явно сообщить его frontend.
Health находится вне `/api/v1`.

HTTPS и публичный адрес production предоставляет оператор. Порт контейнера 8080
не задаёт адрес браузерного клиента. OpenAPI `servers: [{ url: "/" }]` не указывает
готовый production endpoint.

### 2.2. Заголовки

| Заголовок запроса | Когда передавать | Значение |
| --- | --- | --- |
| `Accept` | Рекомендуется во всех API-запросах | `application/json, application/problem+json` |
| `Content-Type` | Только для операций с JSON body | `application/json`; допустима кодировка UTF-8 |
| `Authorization` | На защищённых операциях обязательно, на публичном контенте по желанию | `Bearer <accessToken>` |
| `X-Resource-Version` | Изменение/удаление существующей публикации, комментария, категории/тега; команды состояний | Один UUID из последнего разрешённого DTO этого ресурса |
| `X-Anonymous-Visitor` | Только регистрация анонимного просмотра, если ID уже получен | Непрозрачное значение из `ViewResult.anonymousVisitorId` |

`Authorization: Bearer null`, пустой токен, refresh token и несколько значений
Authorization недопустимы. В анонимном режиме заголовок надо полностью опустить.
Ключи Identity, пароль БД и visitor HMAC key ring frontend не нужны.

### 2.3. Тела и форматы ответов

- Успех с телом: `application/json`. Ошибка: `application/problem+json`.
- `201 Created` возвращает созданный DTO и `Location`.
- `204 No Content` не имеет JSON body; нельзя безусловно вызывать `response.json()`.
- GET, DELETE и команды без requestBody отправляются без тела. Даже `{}` и JSON `null`
  являются телом и нарушают контракт. Это касается publish/unpublish, hide/restore,
  PUT/DELETE like и POST views.
- Глобальный лимит JSON body — **8 MiB = 8 388 608 байт**. Превышение: `413`.
- У полей собственные меньшие лимиты; их превышение даёт `400`, если общий envelope
  не превышен. Markdown имеет отдельный лимит после декодирования JSON.
- Неизвестные JSON-свойства, повторяющиеся JSON-ключи, отсутствие обязательного поля,
  неверные типы и `null` вместо non-null поля отклоняются. Нельзя отправлять DTO ответа
  целиком как DTO записи.
- Клиент отправляет имена свойств в `camelCase`, enum — с точным регистром из документа.
- Неподдерживаемый `Accept`, например только `text/html`, приводит к `406`.
- Неизвестный маршрут возвращает `404`, неподдерживаемый метод — `405`.

### 2.4. CORS и кеширование

Оператор задаёт явный `Api:AllowedOrigins`. CORS разрешает заголовки/методы для этих
origins, но не включает credential cookies (`Access-Control-Allow-Credentials`
отсутствует). Рекомендуемый режим fetch для прямого Blog API — `credentials: "omit"`.
Браузер сам выполняет preflight при необходимости.

Контент с optional Bearer, включая анонимные ответы, имеет
`Cache-Control: private, no-store` и `Vary: Authorization`. Защищённые ответы также
не кешируются; текущая реализация устанавливает `private, no-store` и
`Vary: Authorization` для сопоставленных endpoints. Нельзя использовать общий
CDN/SSR cache для персонализированного ответа или сохранять его в service worker.

In-memory кеш frontend возможен как состояние интерфейса, но его ключ должен
учитывать локального пользователя и параметры запроса. При смене пользователя/
выходе приватные DTO и личные реакции очищаются. HTTP-кеширование через ETag,
If-Match и 304 не является механизмом этого API.

`Location` возвращается сервером, однако текущий CORS не публикует этот заголовок
через `Access-Control-Expose-Headers`. В cross-origin браузере используйте `id`
из тела для построения следующего запроса; чтение Location не должно быть обязательным.

## 3. Личность и права

### 3.1. Разделение Blog и Identity

Identity выдаёт access JWT. Blog проверяет его и сопоставляет проверенную пару
`issuer + sub` локальному пользователю. `Me.id` и `author.id` — **локальные UUID Blog**,
а не `sub` Identity. Для сравнения владельца с текущим пользователем используйте
`author.id === me.id`.

Blog не хранит профиль, пароль, access/refresh token и не предоставляет прокси
управления аккаунтом. Вход, подтверждение почты, обновление токена и выход относятся
к отдельному Identity-клиенту; его граница описана в разделе 23.

### 3.2. Роли

Каждый проверенный пользователь имеет неявную роль `Reader`. Повышенные роли
назначает защищённый операторский CLI. Роли из JWT не дают локальных прав.
Author и Moderator совместимы; Admin обладает перечисленными ниже полномочиями
без необходимости отдельно назначать ему Author и Moderator.

| Возможность | Анонимный | Reader | Author | Moderator | Admin |
| --- | --- | --- | --- | --- | --- |
| Читать Published, справочники, публичные комментарии | Да | Да | Да | Да | Да |
| Регистрировать просмотр | Да | Да | Да | Да | Да |
| Получить `/me`, поставить/снять свой лайк | Нет | Да | Да | Да | Да |
| Создать комментарий | Нет | Да | Да | Да | Да |
| Читать приватный контекст своего комментария | Нет | Да | Да | Да | Да |
| Изменить свой Visible-комментарий | Нет | Да | Да | Да | Да |
| Удалить свой комментарий, включая Hidden | Нет | Да | Да | Да | Да |
| Изменить чужой Visible/Hidden-комментарий | Нет | Нет | Нет | Нет | Да |
| Удалить чужой комментарий | Нет | Нет | Нет | Нет | Да |
| Список модерации, hide/restore | Нет | Нет | Нет | Да | Да |
| Создать публикацию | Нет | Нет | Да | Нет | Да |
| Workspace и управление своими материалами | Нет | Нет | Да | Нет | Да |
| Workspace и управление чужими материалами | Нет | Нет | Нет | Нет | Да |
| Создать/переименовать/удалить категорию или тег | Нет | Нет | Нет | Нет | Да |
| Назначить роли через HTTP | Нет | Нет | Нет | Нет | Нет |

Ограничения состояний действуют для всех ролей: Admin не читает Draft через public
detail, не взаимодействует с закрытым материалом через comments/like/views и не
восстанавливает Deleted-комментарий. Для Draft используется workspace.

Moderator без Author не имеет кабинета публикаций. Владение публикацией не даёт
права модерации чужих комментариев. Владелец Hidden-комментария не может его
редактировать, если у него нет Admin; удалить свой Hidden он может.

Роли читаются backend из БД в начале каждой операции без кеша между запросами.
Изменение роли действует после commit для новой операции с тем же access JWT.
Frontend получает роли из `/me`; декодирование JWT не заменяет этот запрос.
UI-проверка роли служит для доступности элементов, сервер всегда проверяет права заново.

### 3.3. Optional Bearer и ошибки авторизации

На публичном контенте возможны два режима: без Authorization — анонимно;
с Bearer — с обязательной проверкой токена и персонализацией, где она предусмотрена.
Предъявленный плохой токен не превращается в анонимный запрос:

| Ситуация | Ответ |
| --- | --- |
| На защищённом endpoint нет Bearer | `401 authentication_required` |
| Токен неверен/истёк/непригоден | `401 invalid_token` |
| Для проверки недоступны необходимые ключи Identity | `503 identity_keys_unavailable` |
| Проверенная личность не имеет права | `403 forbidden` |
| Ресурс отсутствует или скрывается политикой видимости | `404 resource_not_found` |

401 содержит `WWW-Authenticate: Bearer`; HTML-переадресаций на login нет.
Истечение токена и сбой JWKS требуют разной обработки: 503 не означает logout.
Рекомендуется обновлять токен через Identity одним общим запросом для параллельных
вызовов и не создавать бесконечный цикл 401 → refresh → повтор.

Blog проверяет RS256 JWT с точными issuer/audience и clock skew 30 секунд,
а не опрашивает сессию Identity. Logout/деактивация в Identity может не прекратить
приём уже выданного access JWT до `exp + 30 s`. После выхода frontend должен
немедленно прекратить отправку токена и очистить локальное состояние.
Фиксированный срок access token в 900 секунд не задан.

## 4. Общие типы и валидация

### 4.1. Скалярные типы

| Тип | Представление и правила |
| --- | --- |
| UUID | JSON string; передавать канонический вид `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` (D), включая version/header/query |
| UTC datetime | RFC 3339 string с `Z`, возможны дробные секунды; например `2026-10-27T06:30:00Z` |
| Счётчик | JSON integer, `int64`, минимум 0, максимум типа 9 223 372 036 854 775 807 |
| Enum | JSON string, значения и регистр фиксированы |
| Nullable | Поле ответа присутствует со значением `null`, если значение отсутствует |

UUID/version непрозрачны: клиент их не генерирует вместо серверных, не сортирует
version и не вычисляет следующую. Даты назначает сервер; frontend переводит UTC
в часовой пояс пользователя только для отображения.

JavaScript `number` точно представляет целые только до `Number.MAX_SAFE_INTEGER`
(9 007 199 254 740 991). Сервер отдаёт счётчики числами, не строками. Тип `Int64 = number`
в приложенном файле описывает обычный wire-клиент и сам по себе не обеспечивает
полную int64-точность. Если она нужна, читайте response text и используйте согласованный
lossless JSON parser до преобразования токенов в JS number; затем внутренний bigint/
decimal string. `BigInt()` после обычного `JSON.parse()` не исправляет потерю точности.

### 4.2. Текстовые ограничения

| Поле | Ограничение | Нормализация/особенности |
| --- | --- | --- |
| `title` публикации | 1–200 Unicode scalar values | Сервер обрезает граничные Unicode whitespace |
| `slug` | 1–120, `^[a-z0-9]+(-[a-z0-9]+)*$` | Строгий ввод; сервер не trim-ит и не переводит регистр |
| `description` | `null` или 0–500 Unicode scalar values | Пустая строка допустима; автоматический trim не задан |
| `markdown` | До 1 048 576 UTF-8 байт после JSON decoding | Пустой/whitespace допустим только в Draft; HTML доверенным не становится |
| `coverUrl` | `null` или абсолютный HTTPS URL с host, 1–2048 scalar values | Файл не загружается и не проверяется |
| `name` категории/тега | 1–80 scalar values после Unicode trim | Имена не являются ключом уникальности |
| `content` комментария | 1–5000 scalar values после Unicode trim | Простой недоверенный текст; не Markdown |
| `categoryIds` | Массив 0–5 UUID | Без повторов, все записи должны существовать |
| `tagIds` | Массив 0–20 UUID | Без повторов, все записи должны существовать |

Scalar value — кодовая точка Unicode, а не UTF-16 code unit и не графема.
Валидная emoji вне BMP считается одной scalar value, составная emoji может занимать
несколько. В JS `text.length` недостаточно. Для валидной строки scalar count можно
оценить через `[...text].length`, размер Markdown — через
`new TextEncoder().encode(markdown).byteLength`.
NUL (`U+0000`) и некорректные surrogate-последовательности не допускаются.

Серверный trim для title/name/content и проверки непустоты Markdown использует
следующий набор кодовых точек: `U+0009–U+000D`, `U+0020`, `U+0085`, `U+00A0`,
`U+1680`, `U+2000–U+200A`, `U+2028`, `U+2029`, `U+202F`, `U+205F`, `U+3000`.
Он не обязан совпадать с JS `String.prototype.trim()` во всех пограничных случаях.
Markdown сохраняется исходным; trim используется для проверки непустоты при публикации.
Frontend-валидация дополняет серверную, ошибки сервера остаются окончательными.

Примеры slug: `hello-world`, `post-2026`, `a` — допустимы;
`Hello`, `привет`, `two--words`, `-start`, `end-`, ` hello ` — недопустимы.

## 5. Пагинация, фильтры и сортировка

### 5.1. Общая пагинация

Все растущие коллекции возвращают:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "hasNextPage": false
}
```

| Параметр | По умолчанию | Допустимое значение |
| --- | --- | --- |
| `page` | `1` | Целое от 1 до 2 147 483 647 |
| `pageSize` | `20` | Целое 1–100 |

Параметры передаются по одному значению: `?page=2&pageSize=20`. Пустая строка,
дроби, отрицательные числа, знак `+`, пробелы, значения вне диапазона и повтор
одного scalar query-параметра недопустимы. Дальняя корректная страница даёт
`200`, пустой `items` и `hasNextPage=false`, а не 404.

`total`, `totalPages`, `nextCursor` и URL следующей страницы отсутствуют.
Для «Загрузить ещё» увеличивайте page при `hasNextPage=true`. Для нового фильтра
сбрасывайте page в 1. Справочники тоже постраничные; первая страница не гарантирует
полный набор для формы выбора категорий/тегов.

Это offset-пагинация по текущим данным, без snapshot между запросами. При новых
публикациях/комментариях, изменении лайков или модерации страницы могут сдвигаться.
Рекомендация frontend: при объединении страниц устранять дубликаты по `id`, давать
обновить список и не обещать неизменный порядок при параллельных изменениях.

### 5.2. Сортировки

| Коллекция | Фиксированный порядок |
| --- | --- |
| Feed, `sort=recent` или sort отсутствует | `firstPublishedAt DESC, id DESC` |
| Feed, `sort=popular` | `likeCount DESC, firstPublishedAt DESC, id DESC` |
| Workspace | `updatedAt DESC, id DESC` |
| Категории/теги | `name ASC, id ASC`, серверная collation `C` |
| Корни комментариев и прямые ответы | `createdAt ASC, id ASC` |
| Список модерации | `createdAt DESC, id DESC` |

Recent означает первую публикацию: редактирование и повторная публикация не делают
материал новым. Popular использует текущее количество лайков за всё время.
Клиент сохраняет порядок сервера; локальная locale-сортировка справочников может
не совпасть с серверной.

### 5.3. Фильтры ленты

```http
GET /api/v1/publications?page=1&pageSize=20&sort=recent&categoryIds=20000000-0000-4000-8000-000000000001&categoryIds=20000000-0000-4000-8000-000000000002&tagIds=30000000-0000-4000-8000-000000000001
```

Массивы сериализуются **повторением имени query-параметра**, не CSV, JSON и не
`categoryIds[]`. Пустой массив означает отсутствие параметров, не `categoryIds=`.
Формула выбора:

```text
(категории не выбраны ИЛИ материал имеет хотя бы одну выбранную категорию)
И
(материал имеет каждый выбранный тег)
```

Максимум 5 categoryIds и 20 tagIds; повторы, неверные и неизвестные ID дают 400.
Если все выбранные записи существуют, но ни один материал не подходит, ответ —
пустая страница 200. Лента, публичные комментарии и список модерации отклоняют
неизвестные query-имена. Для остальных маршрутов тоже передавайте только
документированные параметры: произвольные query-поля не добавляют функций.

## 6. Модели данных

Все поля DTO ответа из таблиц обязательны, если не указано иное; nullable-поле
присутствует со значением `null`. В write DTO необязательность указана отдельно.
Исчерпывающие декларации без runtime-валидации находятся в
[frontend-api-types.ts](frontend-api-types.ts).

### 6.1. `AuthorRef`, `Me`, `Taxonomy`, `Counters`

| DTO | Поля |
| --- | --- |
| `AuthorRef` | `id: UUID` |
| `Me` | `id: UUID`, `roles: LocalRole[]` |
| `Taxonomy` | `id: UUID`, `name: string`, `slug: string`, `version: UUID` |
| `Counters` | `likeCount: int64`, `viewCount: int64`, `isLikedByCurrentUser: boolean` |

`AuthorRef` не содержит displayName/avatar/profileUrl. Нельзя строить ссылку
на профиль Identity по этому ID. При отсутствии отдельного профильного сервиса
дизайн должен обходиться без выдуманных имён и аватаров.

В `Me.roles` 1–4 уникальные роли, порядок `Reader`, `Author`, `Moderator`, `Admin`.
`Taxonomy.version` есть и в публичных ответах, и во вложенных категориях/тегах.
Публичная версия справочника не даёт права его изменять.
У анонимного клиента `isLikedByCurrentUser` всегда `false`.

### 6.2. `PublicationCard`

| Поле | Тип | Значение |
| --- | --- | --- |
| `id` | UUID | ID публикации |
| `title` | string | Заголовок после trim |
| `slug` | string | Адресный slug |
| `description` | string/null | Краткое описание |
| `coverUrl` | string/null | Внешняя HTTPS-обложка |
| `author` | AuthorRef | Локальный автор |
| `createdAt` | UTC datetime | Создание Draft |
| `updatedAt` | UTC datetime | Последнее изменение материала |
| `firstPublishedAt` | UTC datetime | Время первой публикации, всегда non-null в public DTO |
| `categories` | Taxonomy[] | 0–5 элементов |
| `tags` | Taxonomy[] | 0–20 элементов |
| `likeCount` | int64 | Число лайков |
| `viewCount` | int64 | Число учтённых просмотров |
| `isLikedByCurrentUser` | boolean | Лайк проверенного текущего пользователя |

Карточка не содержит `markdown`, `state`, `version`, число комментариев или профиль
автора. Вложенные categories/tags возвращаются по имени/ID, а не в порядке выбора.

### 6.3. `Publication`, `WorkspaceCard`, `WorkspacePublication`

| DTO | Состав |
| --- | --- |
| `Publication` | Все поля PublicationCard + `markdown: string` |
| `WorkspaceCard` | Поля PublicationCard, но `firstPublishedAt: UTC datetime/null`, дополнительно `state: Draft/Published`, `version: UUID` |
| `WorkspacePublication` | Все поля WorkspaceCard + `markdown: string` |

В `WorkspaceCard` Markdown отсутствует. В никогда не опубликованном Draft
`firstPublishedAt=null`; у снятого с публикации Draft значение сохраняется.
У Published оно non-null. Deleted не является значением PublicationState в DTO:
удалённая публикация исключена из доступных представлений.

### 6.4. `PublicationWrite`

```json
{
  "title": "Первый материал",
  "slug": "first-post",
  "markdown": "# Первый материал\n\nТекст публикации.",
  "description": "Краткое описание",
  "coverUrl": "https://cdn.example.org/covers/first-post.webp",
  "categoryIds": ["20000000-0000-4000-8000-000000000001"],
  "tagIds": ["30000000-0000-4000-8000-000000000001"]
}
```

Обязательны `title`, `slug`, `markdown`, `categoryIds`, `tagIds`.
`description` и `coverUrl` необязательны и nullable. При PUT их отсутствие
**сбрасывает значение в null**, а не сохраняет предыдущее. PUT полностью заменяет
редактируемые поля. Массивы передаются всегда, в том числе `[]`.

Нельзя передавать `id`, `author`, `authorId`, `state`, даты, счётчики и `version`.
Version передаётся в заголовке существующего ресурса, состояние меняется командой.
Для создания версия не требуется.

### 6.5. `PublicComment`

Общие поля всех публичных узлов:

| Поле | Тип | Значение |
| --- | --- | --- |
| `id` | UUID | ID комментария |
| `publicationId` | UUID | Родительская публикация |
| `parentId` | UUID/null | null у корня |
| `depth` | integer 1–5 | Корень = 1 |
| `createdAt` | UTC datetime | Создание |
| `editedAt` | UTC datetime/null | Последнее изменение текста; null, если текст не редактировали |
| `hasReplies` | boolean | Есть прямые дочерние узлы, включаемые в публичное дерево |
| `state` | Visible/Hidden/Deleted | Видимость/состояние узла |
| `content` | string/null | Зависит от состояния |
| `author` | AuthorRef/null | Зависит от состояния |

Это discriminated union по `state`:

| Вариант | `content` | `author` | Условие включения |
| --- | --- | --- | --- |
| VisibleComment, `Visible` | Непустой текст | `{ id }` | Видимый комментарий |
| CommentPlaceholder, `Hidden` | null | null | Есть хотя бы один видимый потомок |
| CommentPlaceholder, `Deleted` | null | null | Есть хотя бы один видимый потомок |

Публичный DTO **не содержит version**. `hasReplies` не равно количеству ответов,
не сообщает общее число потомков и не содержит вложенного `replies`.

### 6.6. `CommentEditContext` и `ModerationComment`

Оба DTO содержат:
`id`, `publicationId`, `parentId`, `depth`, `state`, `content`, `author`,
`createdAt`, `editedAt`, `version`.
Типы как у публичного комментария, но `author` здесь всегда AuthorRef,
`version` обязателен, а `hasReplies` отсутствует.

| Состояние | EditContext владельца без Admin | EditContext Admin | ModerationComment |
| --- | --- | --- | --- |
| Visible | Текст | Текст | Текст |
| Hidden | `content=null` | Сохранённый текст | Сохранённый текст |
| Deleted | `content=null` | `content=null` | `content=null` |

Нельзя помещать приватный DTO в кеш публичного дерева: так можно раскрыть Hidden
content/author. Даже Admin в публичном списке получает обычную обезличенную заглушку.

### 6.7. Команды комментариев и справочников

```ts
type TaxonomyCreate = { name: string; slug: string };
type TaxonomyRename = { name: string };
type CommentCreate = { content: string; parentId?: string | null };
type CommentUpdate = { content: string };
```

Отсутствующий или null parentId создаёт корневой комментарий. В GET списка корней
parentId надо **опустить**, а не посылать `parentId=null` строкой.
У существующего комментария нельзя изменить публикацию, родителя и автора.
Slug справочника после создания изменить нельзя; rename принимает только name.

### 6.8. `ViewResult`, `Health`, `Problem`

```ts
type ViewResult = {
  counted: boolean;
  viewCount: number; // wire int64, см. оговорку о точности
  anonymousVisitorId: string | null;
};
type Health = { status: "live" | "ready" };
```

ViewResult не содержит likeCount и личный лайк: обновление результата views
заменяет только viewCount. Структура Problem подробно описана в разделе 17.

## 7. Каталог операций

Обозначения доступа: **Public** — без токена или optional Bearer;
**Reader** — любая проверенная локальная роль; **Owner** — владелец ресурса;
**A** — Author; **M** — Moderator; **Admin** — Admin.
Все пути и методы ниже точные. Полный набор статус-кодов каждой операции — в разделе 24.

| № | Метод и путь | operationId | Доступ | Body / версия | Успех |
| --- | --- | --- | --- | --- | --- |
| 1 | `GET /api/v1/me` | `getMe` | Reader | Нет / нет | 200 Me |
| 2 | `GET /api/v1/publications` | `listPublications` | Public | Нет / нет | 200 PublicationPage |
| 3 | `POST /api/v1/publications` | `createPublication` | A/Admin | PublicationWrite / нет | 201 WorkspacePublication |
| 4 | `GET /api/v1/publications/by-slug/{slug}` | `getPublicationBySlug` | Public | Нет / нет | 200 Publication |
| 5 | `GET /api/v1/publications/{id}` | `getPublication` | Public | Нет / нет | 200 Publication |
| 6 | `PUT /api/v1/publications/{id}` | `updatePublication` | Owner+A/Admin | PublicationWrite / да | 200 WorkspacePublication |
| 7 | `DELETE /api/v1/publications/{id}` | `deletePublication` | Owner+A/Admin | Нет / да | 204 |
| 8 | `POST /api/v1/publications/{id}/publish` | `publishPublication` | Owner+A/Admin | Нет / да | 200 WorkspacePublication |
| 9 | `POST /api/v1/publications/{id}/unpublish` | `unpublishPublication` | Owner+A/Admin | Нет / да | 200 WorkspacePublication |
| 10 | `GET /api/v1/workspace/publications` | `listWorkspacePublications` | A: свои; Admin: все | Нет / нет | 200 WorkspacePage |
| 11 | `GET /api/v1/workspace/publications/{id}` | `getWorkspacePublication` | Owner+A/Admin | Нет / нет | 200 WorkspacePublication |
| 12 | `GET /api/v1/categories` | `listCategories` | Public | Нет / нет | 200 TaxonomyPage |
| 13 | `POST /api/v1/categories` | `createCategory` | Admin | TaxonomyCreate / нет | 201 Taxonomy |
| 14 | `GET /api/v1/categories/{id}` | `getCategory` | Public | Нет / нет | 200 Taxonomy |
| 15 | `PUT /api/v1/categories/{id}` | `renameCategory` | Admin | TaxonomyRename / да | 200 Taxonomy |
| 16 | `DELETE /api/v1/categories/{id}` | `deleteCategory` | Admin | Нет / да | 204 |
| 17 | `GET /api/v1/tags` | `listTags` | Public | Нет / нет | 200 TaxonomyPage |
| 18 | `POST /api/v1/tags` | `createTag` | Admin | TaxonomyCreate / нет | 201 Taxonomy |
| 19 | `GET /api/v1/tags/{id}` | `getTag` | Public | Нет / нет | 200 Taxonomy |
| 20 | `PUT /api/v1/tags/{id}` | `renameTag` | Admin | TaxonomyRename / да | 200 Taxonomy |
| 21 | `DELETE /api/v1/tags/{id}` | `deleteTag` | Admin | Нет / да | 204 |
| 22 | `GET /api/v1/publications/{id}/comments` | `listComments` | Public | Нет / нет | 200 CommentPage |
| 23 | `POST /api/v1/publications/{id}/comments` | `createComment` | Reader | CommentCreate / нет | 201 CommentEditContext |
| 24 | `GET /api/v1/comments/{commentId}` | `getCommentEditContext` | Owner/Admin | Нет / нет | 200 CommentEditContext |
| 25 | `PUT /api/v1/comments/{commentId}` | `updateComment` | Owner/Admin, с ограничением state | CommentUpdate / да | 200 CommentEditContext |
| 26 | `DELETE /api/v1/comments/{commentId}` | `deleteComment` | Owner/Admin | Нет / да | 204 |
| 27 | `GET /api/v1/moderation/comments` | `listModerationComments` | M/Admin | Нет / нет | 200 ModerationPage |
| 28 | `POST /api/v1/comments/{commentId}/hide` | `hideComment` | M/Admin | Нет / да | 200 ModerationComment |
| 29 | `POST /api/v1/comments/{commentId}/restore` | `restoreComment` | M/Admin | Нет / да | 200 ModerationComment |
| 30 | `GET /api/v1/publications/{id}/counters` | `getCounters` | Public | Нет / нет | 200 Counters |
| 31 | `PUT /api/v1/publications/{id}/like` | `setLike` | Reader | Нет / нет | 200 Counters |
| 32 | `DELETE /api/v1/publications/{id}/like` | `removeLike` | Reader | Нет / нет | 200 Counters |
| 33 | `POST /api/v1/publications/{id}/views` | `registerView` | Public | Нет / нет | 200 ViewResult |
| 34 | `GET /health/live` | `healthLive` | Без Bearer | Нет / нет | 200 Health |
| 35 | `GET /health/ready` | `healthReady` | Без Bearer, внутренний ingress | Нет / нет | 200 Health |

`Owner+A/Admin` означает «владелец с ролью Author либо любой Admin».
Для публикаций одного владения после отзыва Author недостаточно.
Для управления собственными комментариями повышенная роль не требуется.

## 8. Текущий пользователь

### `GET /api/v1/me` — `getMe`

Обязателен Bearer, query и body отсутствуют.

```http
GET /api/v1/me
Authorization: Bearer <accessToken>
Accept: application/json, application/problem+json
```

```json
{
  "id": "10000000-0000-4000-8000-000000000001",
  "roles": ["Reader", "Author"]
}
```

Первое успешное обращение создаёт локальную связь с Reader, если её нет.
Параллельные первые обращения не создают разных пользователей. Такой bootstrap
возможен и при других проверенных обращениях; `/me` — явный способ получить
локальный ID и права для UI.

Ошибки: 401 — нет/неверный токен; 503 — необходимые ключи/БД недоступны;
500 — внутренняя ошибка. Рекомендуемый момент запроса — после login/refresh,
восстановления приложения и перед входом в привилегированную область.
Повышенную роль можно обновить `/me` с тем же JWT после назначения оператором.

## 9. Публичные публикации

### 9.1. `GET /api/v1/publications` — `listPublications`

Query: `page`, `pageSize`, `sort=recent|popular`, повторяемые `categoryIds`, `tagIds`.
Доступ Public. Ответ `200 PublicationPage`, элементы PublicationCard.
Markdown, edit state и publication version не возвращаются. Поддержанные
комбинации и сортировки описаны в разделе 5.

Клиент не может передать state=Draft, authorId, q, search или произвольный sort.
Неизвестные query-параметры ленты дают `400 validation_failed`.

### 9.2. Чтение detail

```http
GET /api/v1/publications/40000000-0000-4000-8000-000000000001
```

или:

```http
GET /api/v1/publications/by-slug/first-post
```

`getPublication` и `getPublicationBySlug` возвращают одинаковую модель
`200 Publication`. ID должен быть UUID; slug — каноническим по правилам раздела 4.

Пример полного публичного ответа:

```json
{
  "id": "40000000-0000-4000-8000-000000000001",
  "title": "Первый материал",
  "slug": "first-post",
  "description": "Краткое описание",
  "coverUrl": null,
  "author": { "id": "10000000-0000-4000-8000-000000000001" },
  "createdAt": "2026-10-27T05:00:00Z",
  "updatedAt": "2026-10-27T06:30:00Z",
  "firstPublishedAt": "2026-10-27T06:30:00Z",
  "categories": [],
  "tags": [],
  "likeCount": 3,
  "viewCount": 17,
  "isLikedByCurrentUser": false,
  "markdown": "# Первый материал\n\nТекст публикации."
}
```

Только Published доступны публично. Draft, Deleted и неизвестный ресурс дают
одинаковый 404 даже владельцу и Admin. Для редактирования нужен workspace.
GET ленты/detail/counters/comments **не регистрирует просмотр**.

Markdown приходит как недоверенный source. Рендеринг, политика ссылок и HTML
санитизация — ответственность frontend; сервер готовый безопасный HTML не выдаёт.

## 10. Workspace и управление публикациями

### 10.1. Рабочий список

```http
GET /api/v1/workspace/publications?page=1&pageSize=20&state=Draft
Authorization: Bearer <accessToken>
```

`listWorkspacePublications` возвращает `200 WorkspacePage`.
`state` необязателен: `Draft` или `Published`; без него возвращаются оба состояния.
Author видит свои материалы, Admin — все. Reader и Moderator без Author получают 403.
Удалённые исключены. Порядок: updatedAt/id DESC. Markdown в карточках нет.
Фильтр authorId и сортировки feed здесь не поддержаны.

### 10.2. Контекст редактора

```http
GET /api/v1/workspace/publications/40000000-0000-4000-8000-000000000001
Authorization: Bearer <accessToken>
```

`getWorkspacePublication` возвращает `200 WorkspacePublication` с Markdown,
состоянием и версией. Это основной источник формы редактирования.
Чужой Draft, неизвестный и удалённый материал дают 404; чужой Published
без Admin даёт 403. Наличие публичного доступа не даёт edit context.

### 10.3. Создание Draft

```http
POST /api/v1/publications
Authorization: Bearer <accessToken>
Content-Type: application/json

{
  "title": "Первый материал",
  "slug": "first-post",
  "markdown": "",
  "description": null,
  "coverUrl": null,
  "categoryIds": [],
  "tagIds": []
}
```

`createPublication`: Author/Admin, `201 WorkspacePublication`.
`Location: /api/v1/workspace/publications/{newId}`.
Автор устанавливается по проверенной личности; Admin также создаёт от своего имени.
Состояние всегда Draft, firstPublishedAt=null, счётчики нового материала 0,
version назначает сервер. Пустой Markdown допустим; title и slug обязательны.
Неизвестная классификация — 400; занятый publication slug — `409 slug_conflict`.

Создание публикации не имеет Idempotency-Key. При потере ответа нельзя слепо
повторять POST; сначала проверьте workspace на результат. Совпавший slug при
повторе не доказывает, что найден именно ваш ресурс.

### 10.4. Сохранение полей

```http
PUT /api/v1/publications/40000000-0000-4000-8000-000000000001
Authorization: Bearer <accessToken>
X-Resource-Version: 60000000-0000-4000-8000-000000000001
Content-Type: application/json

{
  "title": "Обновлённый заголовок",
  "slug": "first-post",
  "markdown": "# Обновлённый текст",
  "description": null,
  "coverUrl": null,
  "categoryIds": [],
  "tagIds": []
}
```

`updatePublication`: владелец с Author или Admin, `200 WorkspacePublication`.
PUT заменяет все редактируемые поля, обновляет version и updatedAt, сохраняет
владельца и состояние. Даже сохранение одинаковых полей не стоит считать no-op:
текущая реализация записи меняет edit version.

Сохранённые правки Published **сразу публичны**. Параллельной staging-редакции нет.
Если интерфейс обещает «сохранить черновик изменений без изменения опубликованного
текста», такой серверной операции сейчас нет.

До первой публикации slug можно менять. После первой публикации он неизменяем
навсегда, в том числе после unpublish. Попытка изменения — `409 invalid_state`.
Published нельзя сохранить с пустым/whitespace Markdown — `400 validation_failed`.

### 10.5. Публикация и снятие с публикации

```http
POST /api/v1/publications/40000000-0000-4000-8000-000000000001/publish
Authorization: Bearer <accessToken>
X-Resource-Version: 60000000-0000-4000-8000-000000000002
```

Body отсутствует. Ответ `200 WorkspacePublication`. Publish требует непустой
после Unicode trim Markdown, назначает firstPublishedAt при первом переходе,
обновляет state/version/updatedAt. Для уже Published с текущей версией успешен
без изменений; старая версия всё равно даёт 409.

Unpublish выполняется аналогично через
`POST /api/v1/publications/{id}/unpublish`. Возвращает Draft с сохранёнными
firstPublishedAt, slug, комментариями, лайками и просмотрами. Публичное чтение и
взаимодействия становятся недоступны, включая для Admin. Повторное publish
возвращает доступность, не меняя время первой публикации.

| Исходное состояние | Команда | Результат |
| --- | --- | --- |
| Draft | publish, Markdown непустой | Published, первая дата назначается только один раз |
| Published | publish с текущей версией | Успех, без изменения version/дат |
| Published | unpublish | Draft, публичный доступ закрыт |
| Draft | unpublish с текущей версией | Успех, без изменения version/дат |
| Draft/Published | delete | Терминальное удаление |
| Удалённый | Любое чтение/изменение материала | 404 |

### 10.6. Удаление публикации

`DELETE /api/v1/publications/{id}`, Bearer и текущий X-Resource-Version, без body.
Успех `204`. Материал исчезает из workspace и public API; все дочерние чтения/
взаимодействия закрываются. Связи с категориями/тегами снимаются. Slug остаётся
занятым навсегда. Restore API нет; повторный DELETE удалённого ресурса даёт 404.
Рекомендация UI: перед действием явно объяснять необратимость.

## 11. Категории и теги

Это два независимых плоских справочника с одинаковым контрактом.
Slug уникален внутри своего справочника, а не глобально между тегами,
категориями и публикациями. Имя может совпадать; сервер не гарантирует уникальное name.

### 11.1. Чтение

| Операция | Query | Ответ |
| --- | --- | --- |
| `GET /api/v1/categories` — listCategories | page/pageSize | 200 TaxonomyPage |
| `GET /api/v1/categories/{id}` — getCategory | Нет | 200 Taxonomy |
| `GET /api/v1/tags` — listTags | page/pageSize | 200 TaxonomyPage |
| `GET /api/v1/tags/{id}` — getTag | Нет | 200 Taxonomy |

Public с optional Bearer. Список отсортирован name/id ASC. Неизвестный ID — 404.
Нет эндпоинта справочника по slug, дерева категорий, search, usageCount или списка
материалов внутри DTO; для публикаций вызывается feed с фильтром ID.

### 11.2. Создание

`POST /api/v1/categories` — createCategory; `POST /api/v1/tags` — createTag.
Только Admin, body TaxonomyCreate, версия не нужна:

```json
{ "name": "Разработка", "slug": "development" }
```

Ответ `201 Taxonomy`, `Location: /api/v1/categories/{id}` либо `/api/v1/tags/{id}`.
Занятый slug — `409 slug_conflict`. После получения DTO сохраняйте его version
для последующих Admin-команд.

### 11.3. Переименование

`PUT /api/v1/categories/{id}` — renameCategory;
`PUT /api/v1/tags/{id}` — renameTag. Только Admin, текущая version в заголовке.

```json
{ "name": "Инженерная практика" }
```

Ответ `200 Taxonomy` с новой version. Slug не меняется; передавать slug/id/version
в body нельзя. Переименование в то же name не гарантирует сохранения version.

### 11.4. Удаление

`DELETE /api/v1/categories/{id}` — deleteCategory;
`DELETE /api/v1/tags/{id}` — deleteTag. Только Admin, version обязательна,
body отсутствует. Успех `204`.

Если запись связана хотя бы с одним неудалённым материалом, включая Draft,
возвращается `409 taxonomy_in_use`. Автоматического отвязывания или каскадного
удаления публикаций нет. Чтобы удалить запись, надо сначала снять связи в
публикациях полным PUT или удалить соответствующие материалы.
Отсутствующая запись — 404, устаревшая версия — 409 concurrency_conflict.

## 12. Комментарии

Все операции комментариев требуют доступного Published-материала. Unpublish или
удаление публикации делает недоступными и публичные, и приватные comment endpoints.
Комментарии — простой текст, не Markdown и не HTML; новые Visible появляются сразу,
очереди предварительного одобрения нет.

### 12.1. Корни и прямые ответы

```http
GET /api/v1/publications/40000000-0000-4000-8000-000000000001/comments?page=1&pageSize=20
```

`listComments`, Public. Без parentId — только корневые узлы.
Для раскрытия ответов:

```http
GET /api/v1/publications/40000000-0000-4000-8000-000000000001/comments?parentId=50000000-0000-4000-8000-000000000001&page=1&pageSize=20
```

Ответ `200 CommentPage`, только прямые дети указанного узла. У каждой ветки
собственные page/pageSize/hasNextPage. Полное поддерево сервер не разворачивает.
Порядок createdAt/id ASC, то есть старые первыми.

parentId должен принадлежать этому материалу и быть включаемым в публичное дерево.
Неизвестный/чужой родитель и Hidden/Deleted без видимых потомков дают 404.
Неверный UUID/пустой parentId, повтор scalar-параметра и неизвестный query — 400.

Пример узла-заглушки:

```json
{
  "id": "50000000-0000-4000-8000-000000000001",
  "publicationId": "40000000-0000-4000-8000-000000000001",
  "parentId": null,
  "depth": 1,
  "createdAt": "2026-10-27T07:00:00Z",
  "editedAt": null,
  "hasReplies": true,
  "state": "Hidden",
  "content": null,
  "author": null
}
```

Hidden/Deleted без видимых потомков полностью пропускаются. Если есть видимый
внук через скрытого ребёнка, структурные заглушки сохраняют путь к нему.
Дизайн обязан уметь показывать «Комментарий скрыт»/«Комментарий удалён» с кнопкой
раскрытия ответов. Текст/автор заглушки не восстанавливаются из старого публичного
кеша или приватного DTO.

### 12.2. Создание комментария или ответа

`POST /api/v1/publications/{id}/comments` — createComment, Bearer Reader.
Для корня:

```json
{ "content": "Спасибо за материал.", "parentId": null }
```

Для ответа:

```json
{
  "content": "Уточнение к вашему комментарию.",
  "parentId": "50000000-0000-4000-8000-000000000001"
}
```

Ответ `201 CommentEditContext`,
`Location: /api/v1/comments/{newCommentId}`. Состояние Visible, editedAt=null,
автор — текущий локальный пользователь. Version родителя не требуется.

Условия ответа: родитель того же материала, доступен, **Visible**, глубина < 5.
Корень depth=1, пятый уровень конечный. Ответ на доступную Hidden/Deleted заглушку
или на узел глубины 5 — `409 invalid_state`. Отсутствующий/чужой/публично
недоступный родитель — 404. Текст после trim 1–5000 — иначе 400.

Сервер не поддерживает Idempotency-Key для createComment. Двойной клик/слепой
сетевой повтор может создать два комментария. Рекомендуется блокировать submit
пока запрос выполняется и осторожно разбирать неопределённый результат.

### 12.3. Получение edit context

`GET /api/v1/comments/{commentId}` — getCommentEditContext, Owner или Admin.
Ответ `200 CommentEditContext`; это **не публичное чтение комментария по ID**.
Чужой доступный комментарий без Admin — 403. Moderator читает чужой скрытый текст
через список модерации, а не через этот маршрут.

Для своего Hidden владелец получает content=null и свежую version: их достаточно
для удаления. Версию нельзя брать из публичного дерева — её там нет.

### 12.4. Редактирование

`PUT /api/v1/comments/{commentId}` — updateComment, Bearer и X-Resource-Version.

```json
{ "content": "Исправленный текст комментария." }
```

Ответ `200 CommentEditContext` с новой version и editedAt.
Visible может изменить владелец или Admin; правка сразу публична.
Hidden может изменить только Admin, при этом состояние остаётся Hidden.
Deleted изменить нельзя — `409 invalid_state` при наличии права и текущей версии.
Чужой комментарий и Hidden для обычного владельца — 403.
parentId, depth, author, publicationId, state и даты неизменяемы через PUT.

### 12.5. Удаление

`DELETE /api/v1/comments/{commentId}` — deleteComment, Owner/Admin,
X-Resource-Version обязателен, body отсутствует. Ответ `204`.
Текст необратимо очищается, состояние Deleted, структура/ответы сохраняются.
Admin может удалить чужой комментарий; Moderator без Admin — только собственный.

Повторное удаление Deleted с **текущей** версией успешно без смены версии/дат.
Первое удаление меняет версию, а 204 её не возвращает: для последующего осознанного
повтора требуется новый GET edit context. Повтор со старой version даст 409.
Restore Deleted отсутствует; hide/restore относится только к модерационному Hidden.

## 13. Модерация

### 13.1. Список модерации

```http
GET /api/v1/moderation/comments?page=1&pageSize=20&state=Hidden&publicationId=40000000-0000-4000-8000-000000000001
Authorization: Bearer <accessToken>
```

`listModerationComments`, Moderator/Admin, `200 ModerationPage`.
Все query-поля необязательны: page/pageSize стандартные,
state=Visible|Hidden|Deleted, publicationId=UUID. Без state — все состояния;
без publicationId — комментарии всех Published-материалов.
Порядок createdAt/id DESC. Неизвестные query-поля отклоняются.

Список возвращает сохранённый Hidden content/author и version. Deleted content=null,
author остаётся локальной ссылкой. Скрытые/удалённые узлы без видимых потомков
в этом приватном списке тоже могут присутствовать.
Если явно задан Draft/Deleted/отсутствующий publicationId — 404, а не пустая страница.
Нет предварительного approve, pending, жалоб, поисковой строки, reason поля или
массового endpoint. Название экрана лучше отражает последующую модерацию.

### 13.2. Hide и restore

```http
POST /api/v1/comments/50000000-0000-4000-8000-000000000001/hide
Authorization: Bearer <accessToken>
X-Resource-Version: 70000000-0000-4000-8000-000000000001
```

`hideComment` и `restoreComment` вызываются без body.
Restore отличается суффиксом `/restore`. Оба доступны Moderator/Admin и возвращают
`200 ModerationComment`, включая актуальную version.

| Исходное состояние | hide | restore |
| --- | --- | --- |
| Visible | Hidden, сохраняет текст/автора | Успех без изменения состояния/version/дат |
| Hidden | Успех без изменения состояния/version/дат | Visible, возвращает сохранённый текст публично |
| Deleted | 409 invalid_state | 409 invalid_state |

Текущая version нужна и для no-op; устаревшая всегда даёт 409.
Hide/restore не меняет текст и editedAt. Меняется видимость одного узла,
видимые потомки остаются доступны. Сервер атомарно обновляет структурную
доступность предков; frontend перечитывает затронутые ветки, а не скрывает
безусловно всё поддерево.

## 14. Лайки и счётчики

### 14.1. Текущие значения

`GET /api/v1/publications/{id}/counters` — getCounters, Public.

```json
{ "likeCount": 4, "viewCount": 18, "isLikedByCurrentUser": true }
```

Ответ `200 Counters`. Для личного состояния нужен Bearer; без него false.
GET не записывает просмотр. Draft/Deleted/неизвестный материал — 404 при любой роли.

### 14.2. Установить или снять свой лайк

```http
PUT /api/v1/publications/40000000-0000-4000-8000-000000000001/like
Authorization: Bearer <accessToken>
```

Установить — `setLike`, PUT; снять — `removeLike`, DELETE того же пути.
Body и X-Resource-Version не требуются. Оба возвращают **200 Counters**, а не 204.
Одна пара publication/currentUser имеет не более одного лайка. Это команды
желаемого состояния, а не toggle:

- Повтор PUT при уже установленном лайке успешен без дополнительного увеличения.
- Повтор DELETE при отсутствии лайка успешен без уменьшения ниже нуля.
- Параллельные повторные операции не создают дубликаты.
- Лайки не меняют version, updatedAt и контент публикации.
- Popular отражает текущие изменения после записи.

Рекомендация frontend: либо блокировать кнопку на время команды, либо
сериализовать намерения пользователя, чтобы поздний ответ не вернул старое UI-состояние.
Оптимистический UI допустим при откате/сверке с серверным Counters и обновлении
копий публикации в feed/detail. После входа перечитайте Counters с Bearer.

## 15. Просмотры

### 15.1. Регистрация открытия

```http
POST /api/v1/publications/40000000-0000-4000-8000-000000000001/views
X-Anonymous-Visitor: <previously-issued-opaque-token>
```

`registerView`, Public, **без body**, без resource version.

Первое успешное анонимное открытие:

```json
{
  "counted": true,
  "viewCount": 19,
  "anonymousVisitorId": "<engine-issued-opaque-token>"
}
```

Для проверенного пользователя:

```json
{ "counted": false, "viewCount": 19, "anonymousVisitorId": null }
```

`counted` означает, увеличен ли счётчик этим запросом. `false` — обычный успешный
повтор в окне, не ошибка. viewCount — текущий общий счётчик материала.

### 15.2. Анонимный идентификатор

Первый анонимный вызов без X-Anonymous-Visitor получает ID в теле успешного ответа.
Frontend обязан сохранить его и передавать в следующих views-запросах к этой
установке для всех материалов. Cookie API не устанавливает.

Это непрозрачный токен, не UUID пользователя и не credential аккаунта. Не нужно
его генерировать, разбирать, подписывать или отправлять в Identity.
Отсутствующий/непригодный ID, несколько значений header или строка длиннее
256 символов приводят к выдаче нового ID при успешной регистрации.
Общие лимиты размера HTTP-заголовков действуют отдельно.

Рекомендация: хранить ID под ключом, изолированным по установке, например
`blog:<installation>:anonymousVisitorId`. Local storage или first-party cookie
потребителя — архитектурный выбор frontend, сервер его не определяет.
Если браузерное хранилище недоступно, возможен временный in-memory ID с потерей
стабильности при перезагрузке. Потеря/очистка ID делает посетителя новым.

Не стирайте сохранённый анонимный ID только из-за `anonymousVisitorId=null`
в ответе с Bearer: это normal authenticated mode, старый анонимный ID может
понадобиться после выхода. Однако не отправляйте его как личность вошедшего
пользователя — сервер использует проверенный локальный ID.

### 15.3. Правило 24 часов

Ключ dedup — публикация + посетитель. После учтённого времени T повтор до
`T + 24 h` возвращает counted=false и не сдвигает окно. В точности на границе
может быть учтён один новый просмотр. Для вошедшего пользователя ключ общий
между устройствами. Анонимная история после входа не объединяется с личной.

Draft/Deleted/неизвестный материал — 404, без выдачи нового token и без записи
просмотра. Невалидный Bearer — отказ, даже если есть анонимный ID.

Рекомендации frontend:

1. Вызывать views только после успешного открытия detail пользователем.
2. Не учитывать карточки ленты, предварительную загрузку ссылок и SSR prefetch
   как открытия. При SSR выполнить учёт в браузере один раз по принятой политике UI.
3. Не вызывать views во время render; контролировать повтор effects/повторную mount.
4. Сначала получить/сохранить общий ID, затем параллелить другие анонимные views:
   несколько первых запросов без ID могут выпустить разных анонимных посетителей.
5. Обновлять viewCount по ответу; остальные Counters из ViewResult не выводить.
6. Ошибку необязательного учёта показывать независимо от успешно загруженного текста;
   она не должна заменять весь материал экраном ошибки.

## 16. Health и служебная документация

`GET /health/live` — healthLive: `200 { "status": "live" }`, только живость процесса,
без проверки БД/Identity.

`GET /health/ready` — healthReady: `200 { "status": "ready" }` при доступном
PostgreSQL и пригодном свежем JWKS snapshot; иначе 503 Problem.
Fresh key cache может сохранять готовность при временной недоступности Identity.
Readiness предназначен для внутреннего ingress/эксплуатации, не для проверки
пользовательского входа. Запросы health отправляйте без Authorization.

Development дополнительно публикует `/openapi/v1.json` и `/swagger/index.html`.
В Production они выключены; frontend-агент использует YAML из репозитория и
не должен зависеть от runtime Swagger для работы приложения.

## 17. Ошибки

### 17.1. Формат Problem

```json
{
  "type": "about:blank",
  "title": "Invalid request",
  "status": 400,
  "code": "validation_failed",
  "traceId": "00-<trace>-<span>-01",
  "errors": {
    "title": ["Invalid value."]
  }
}
```

Обязательны type/title/status/code/traceId. `detail` и `errors` необязательны.
`errors` — map имени поля в массив сообщений. Дополнительные Problem extensions
допустимы. Сервер может сообщать только первую найденную ошибку; клиент не должен
ожидать полный набор или errors в каждом 400. Ошибки malformed JSON/unknown fields
могут приходить как invalid_request без привязки к полю.

Для ветвления используйте HTTP status и стабильный code, а не английский title/
detail/строку `Invalid value.`. Пользовательский русский текст локализуется на
frontend. traceId можно показать в деталях сбоя для обращения к поддержке.

### 17.2. Коды и ожидаемая реакция интерфейса

| HTTP | code | Значение | Рекомендация frontend |
| --- | --- | --- | --- |
| 400 | validation_failed | Нарушение ограничения поля/query/header | Подсветить поле, сохранить ввод; header version error отнести к операции |
| 400 | invalid_request | Непригодный JSON, схема, binding или запрещённое body | Показать общую ошибку; исправить клиентскую сериализацию |
| 401 | authentication_required | На защищённой операции нет Bearer | Предложить вход, сохранить допустимый контекст действия |
| 401 | invalid_token | Неверный/истёкший/неподходящий access JWT | Ограниченный refresh через Identity, затем повтор по правилам операции или вход |
| 403 | forbidden | Личность проверена, права/владение недостаточны | Обновить /me, убрать недоступное действие; не маскировать как 401 |
| 404 | resource_not_found | Нет ресурса или он недоступен | Экран недоступности/удаление устаревшего элемента, без раскрытия Draft |
| 405 | method_not_allowed | Путь существует, метод не поддержан | Ошибка клиента; проверить метод |
| 406 | not_acceptable | Неподдерживаемый Accept | Исправить заголовок запроса |
| 409 | concurrency_conflict | Версия устарела | Сохранить локальный ввод и получить новый edit context для сравнения |
| 409 | slug_conflict | Slug уже занят | Привязать к slug и предложить другой |
| 409 | taxonomy_in_use | Удаляемая категория/тег используется | Объяснить необходимость снять связи |
| 409 | invalid_state | Операция запрещена текущим состоянием | Перечитать ресурс; обновить доступные действия, сохранить ввод |
| 413 | request_too_large | Общий JSON body больше 8 MiB | Уменьшить запрос; не повторять без изменения |
| 415 | unsupported_media_type | Неверный Content-Type для JSON операции | Исправить Content-Type/сериализацию |
| 500 | internal_error | Непредвиденная ошибка | Неблокирующая/страничная ошибка по контексту, traceId, аккуратный retry |
| 503 | dependency_unavailable | Недоступна БД/инфраструктура | Временный сбой, сохранить ввод, retry с задержкой |
| 503 | identity_keys_unavailable | Нельзя безопасно проверить токен | Временный сбой; не сбрасывать сессию как при 401 |

Общие протокольные ошибки могут возникать сверх ответов, явно перечисленных
у операции в OpenAPI. Например, bodyless команда с `{}` даст 400,
а превышенный общий body envelope — 413. Ошибки reverse proxy, DNS, CORS,
отсутствие сети и отмена клиентом могут вообще не иметь Problem JSON.
Обрабатывайте не-JSON/пустой ответ отдельной transport-ошибкой, не вызывая
безусловный JSON parser. Не показывайте пользователю сырой HTML от proxy.

### 17.3. Видимость 403/404 и порядок проверок

Проверка доступа выполняется до раскрытия конфликта version. Недоступный ресурс
не должен превращаться в «версия устарела» из-за переданного заголовка.
Не проектируйте UI, требующий отличить отсутствующий ресурс от скрытого Draft.

| Ситуация | Результат |
| --- | --- |
| Public detail Draft/Deleted/неизвестного, даже владельцем/Admin | 404 |
| Workspace чужого Draft без Admin | 404 |
| Workspace чужого Published без Admin | 403 |
| Дочерняя операция материала после unpublish/delete | 404 |
| Чужой edit context комментария у доступного Published без Admin | 403 |
| Изменение своего Hidden без Admin | 403 |
| Непригодный предъявленный Bearer до проверки ресурса | 401 либо 503 по причине проверки |

## 18. Версии, повтор запросов и гонки

### 18.1. Optimistic concurrency

Последовательность изменения существующего ресурса:

1. Получить разрешённый DTO с `version` — workspace detail, taxonomy DTO,
   comment edit context или moderation DTO.
2. Сохранить версию вместе с загруженным состоянием формы.
3. Отправить запись/команду с `X-Resource-Version: <version>`.
4. При успехе с телом заменить локальное состояние и version DTO ответа.
5. При 204 закрыть форму/удалить элемент; если нужен новый контекст, перечитать его.
6. При 409 concurrency_conflict сохранить локальный ввод, прочитать серверное
   состояние и предложить осознанно сравнить/повторить изменения с новой version.

Нельзя автоматически получить новую version и повторить старую полную запись:
такой цикл может затереть изменения другого автора/Admin. Нет force/overwrite
параметра, patch-операций и merge endpoint.

Публикация, комментарий, категория и тег имеют независимые версии. Version категории
не используется как version публикации. Обновление лайков/просмотров, создание
комментария и модерация не требуют версии публикации и не вращают её edit version.
Модерация комментария меняет его собственную version.

### 18.2. Повтор и неизвестный исход

| Операция | Правило повтора |
| --- | --- |
| GET | Можно повторять с ограниченной задержкой при временном transport/5xx сбое |
| PUT/DELETE like | Повтор того же желаемого состояния безопасен в отношении дельты лайков |
| POST views | Повтор с той же личностью в окне dedup не добавляет второй просмотр |
| POST create publication/comment/taxonomy | Нет универсального Idempotency-Key; не повторять слепо после потери ответа |
| PUT content/rename | После выполненной записи старая version конфликтует; сначала сверить серверное состояние |
| Publish/unpublish/hide/restore | Текущая версия + уже желаемое состояние — no-op; старую версию нельзя переиспользовать автоматически |
| DELETE publication/taxonomy | После успеха ресурс отсутствует; повтор может дать 404 |
| DELETE comment | Deleted + текущая версия — успешный no-op; старая версия даёт 409 |

Сетевая ошибка, отмена fetch и 5xx не всегда доказывают отсутствие commit.
Для спорной записи показывайте возможность проверить серверное состояние;
не создавайте дубликаты фоновым retry. Успешный ответ является актуальной
проекцией на момент чтения сервером, но не блокирует следующие изменения
других клиентов.

## 19. Требования к frontend-интеграции

Этот раздел — рекомендации для frontend, основанные на ограничениях API.

### 19.1. API-слой и данные

- Отделить Blog API client от Identity client: разные base URL, DTO и обработка ошибок.
- Явно поддержать anonymous/optional/required Bearer режимы; отсутствие токена
  не сериализовать в строку заголовка.
- Общим обработчиком разбирать HTTP status, Content-Type, Problem и transport errors;
  204 не парсить как JSON.
- Строить повторяемые фильтры через `URLSearchParams.append()`, scalar-параметры — через set.
- Иметь явные write mappers; не отправлять response object/React form metadata в API.
- Хранить пользовательские cache keys с me.id и очищать их при смене личности.
- Не добавлять скрытый views-вызов в универсальный getPublication: это отдельное действие UI.
- Не отправлять access/refresh token, полный контент и visitor token в аналитические события/логи.

Пример сериализации feed:

```ts
const query = new URLSearchParams();
query.set("page", String(page));
query.set("pageSize", String(pageSize));
query.set("sort", sort);
for (const id of categoryIds) query.append("categoryIds", id);
for (const id of tagIds) query.append("tagIds", id);
const url = `${blogApiOrigin}/api/v1/publications?${query}`;
```

### 19.2. Безопасное отображение

Markdown рендерить согласованным renderer с запретом/санитизацией raw HTML,
проверкой URL-схем в ссылках и изображениях. Комментарии, заголовки, descriptions,
имена таксономий выводить как текст, не через HTML injection.
Внешняя обложка может отсутствовать/не загрузиться; нужен fallback без нарушения
композиции. Сервер не проксирует изображение и не подтверждает его доступность.

Не использовать публичные DTO как источник скрытого текста. Данные moderation/
edit context держать отдельно и очищать после потери прав/выхода. Имена/аватары
не синтезировать из UUID; для них требуется отдельный согласованный контракт.

### 19.3. Обновление UI после операций

| Действие | Что актуализировать |
| --- | --- |
| Login/logout/смена пользователя | Me, личные like state, приватные query caches; публичные данные перечитать в новом auth-контексте |
| Create/update/publish/unpublish/delete публикации | Workspace list/detail, public detail, feed; после unpublish/delete убрать недоступные дочерние экраны |
| Create/rename/delete категории/тега | Списки/селекторы справочников, вложенные labels в публикациях, активные фильтры |
| Create/edit/delete комментария | Соответствующая страница корней/ветки, hasReplies и структурные предки при необходимости |
| Hide/restore | Moderation list/DTO, публичные ветки и заглушки предков, собственный edit context если открыт |
| Like/unlike | Counters в detail/feed; popular list при необходимости |
| Views | Только актуальный viewCount; сохранение анонимного ID |

Новых событий сервер не пушит. Обновление по фокусу, ручное refresh или умеренный
polling — решение frontend. Агрессивный polling всех веток не требуется контрактом.

### 19.4. Состояния экранов

Для коллекций нужны: initial loading, пустой список, ошибка с retry,
полученные элементы, загрузка следующей страницы и ошибка только следующей страницы.
Пустой список отличается от 404 недоступного родительского материала.

Для редактора нужны: loading context, редактирование, несохранённый ввод,
saving, field errors, конфликт version с сохранённым локальным текстом,
потеря прав, недоступный/удалённый ресурс и неизвестный исход записи.
Publish не должен отправлять ещё не сохранённый локальный текст как будто команда
принимает body: сначала PUT/POST Draft, затем publish с version успешного ответа.

Для комментариев нужны: независимая пагинация каждой ветки, plain-text ввод,
Hidden/Deleted placeholders, ограничения depth и состояния кнопки «Ответить».
Для заглушек кнопка ответа недоступна; раскрыть сохранённые ответы можно.

## 20. Пользовательские сценарии

### 20.1. Анонимное чтение

1. Загрузить feed и страницы справочников для фильтров без Authorization.
2. Открыть detail по slug, отрендерить безопасный Markdown.
3. Из браузерного действия вызвать views с сохранённым anonymousVisitorId, если есть.
4. Сохранить выданный ID и обновить viewCount.
5. Загрузить корневые комментарии, раскрывать ответы отдельными запросами.
6. Для лайка/комментария предложить вход; анонимной write-операции нет.

### 20.2. Вход и персональные действия

1. Выполнить login через согласованный внешний Identity client.
2. Передать access JWT в Blog `/me`, получить локальный ID/роли.
3. Перечитать detail/counters с Bearer, отобразить личный лайк.
4. Выполнить PUT/DELETE like и принять Counters сервера.
5. Для своего Visible-комментария получить edit context перед изменением.
6. При logout прекратить отправку Bearer, очистить приватные кеши/несохранённые
   приватные контексты по принятой UX-политике; анонимную реакцию не выдавать за личную.

### 20.3. Создание и публикация автором

1. По `/me` убедиться в Author/Admin, загрузить страницы справочников.
2. POST PublicationWrite с обязательными полями, получить Draft/version.
3. При дальнейших правках использовать PUT с текущей version.
4. Для «Опубликовать» сначала сохранить все актуальные поля.
5. POST publish без тела, с version из успешного сохранения.
6. Перейти на публичный URL по slug из ответа, обновить workspace/feed.
7. После первой публикации заблокировать изменение slug и пояснить постоянство ссылки.

### 20.4. Конфликт двух редакторов

1. Оба загрузили workspace с version V1.
2. Первый сохранил запись, получил V2.
3. Второй отправил PUT с V1, получил 409 concurrency_conflict.
4. Второй сохраняет локальный текст, получает новый workspace DTO, показывает
   различия/выбор пользователя. Автоматическое затирание V2 не выполняется.

### 20.5. Модерация и сохранение обсуждения

1. Moderator получает moderation list с Visible/Hidden и версиями.
2. POST hide с актуальной version, принимает ModerationComment Hidden.
3. Публичное дерево перечитывается: родитель с видимыми ответами становится заглушкой.
4. Автор комментария может получить свой context с content=null/version и удалить его.
5. Restore Hidden выполняется Moderator/Admin; Deleted восстановить нельзя.

### 20.6. Удаление категории Admin

1. Загрузить Taxonomy DTO и version.
2. DELETE с version.
3. При taxonomy_in_use объяснить существующие связи; API не выдаёт список использований.
4. Снять связи в соответствующих публикациях через workspace и полные PUT.
5. Перечитать taxonomy version и повторить удаление при осознанном намерении.

## 21. Контрольный список приёмки

Это сценарии для frontend-агента и mocks. Они не заявляют запуск новой backend-suite
в рамках подготовки документа.

- [ ] Feed/details работают анонимно и с Bearer, не вызывают views при простом GET.
- [ ] Фильтры сериализуются повторением categoryIds/tagIds; комбинация OR категорий
  и AND тегов проверена на данных, неизвестный ID обрабатывается как 400.
- [ ] Корректная пустая страница и отсутствие hasNextPage не выглядят как ошибка.
- [ ] Категории/теги в селекторах подгружаются за пределами первой страницы.
- [ ] Public DTO не используется для редактирования публикации; workspace detail
  возвращает Markdown/version и скрытые Draft владельца.
- [ ] Полный PUT не стирает случайно description/coverUrl/categoryIds/tagIds.
- [ ] Publish сохраняет текущий текст до команды; Published edits сразу видны публично.
- [ ] Slug после первой публикации недоступен для изменения даже у Draft после unpublish.
- [ ] После delete нет кнопки restore; занятый удалённым материалом slug обрабатывается.
- [ ] Unicode/emoji и UTF-8 лимиты не сводятся к JS string.length.
- [ ] Version отправляется в правильном заголовке, обновляется после успеха;
  409 сохраняет локальную форму и не вызывает автоматического overwrite.
- [ ] Два пользователя/выход не разделяют личные реакции или приватный cache.
- [ ] 401, 403, 404 и оба 503 различаются; плохой токен не вызывает скрытый anonymous fallback.
- [ ] Кнопки действий соответствуют локальным ролям, владению и состоянию ресурса.
- [ ] Moderator без Author не получает workspace; владелец публикации без M/Admin
  не получает кнопки hide/restore чужих комментариев.
- [ ] Комментарии показываются как текст; public Hidden/Deleted не раскрывают автора/текст.
- [ ] Ветки комментариев пагинируются независимо; depth=5 и заглушки нельзя использовать для ответа.
- [ ] Собственный Hidden можно удалить после GET context, редактировать без Admin нельзя.
- [ ] Private comment/moderation DTO не попадает в публичную карточку/кеш.
- [ ] Повтор like/unlike не создаёт лишних дельт, поздние ответы не переворачивают UI.
- [ ] Анонимный visitor ID сохраняется и переиспользуется; Bearer имеет приоритет;
  counted=false не отображается ошибкой.
- [ ] Нет повторного views-учёта из render/prefetch; параллельный первый anonymous вызов контролируется.
- [ ] Bodyless запросы не отправляют `{}`/`null`, 204 не парсится как JSON.
- [ ] Отработаны не-JSON proxy error, offline, отмена, ошибка загрузки обложки и retry.
- [ ] Ошибка необязательного views не скрывает успешно загруженный материал.
- [ ] UI не требует от Blog имён, аватаров, поиска, загрузки файлов и управления ролями.
- [ ] Identity logout очищает client state немедленно, без ожидания отказа Blog JWT.

## 22. Настройки и вопросы для интеграции

Эти значения предоставляет владелец установки, они не определяются спецификацией:

| Параметр/решение | Для чего требуется |
| --- | --- |
| Blog origin/path base по средам | Формирование конечных URL |
| Frontend origins по средам | Серверный CORS allowlist, согласование ingress |
| Identity base URL и clientId | Внешний login/register/refresh/logout |
| Audience выбранного Identity client | Совместимость access JWT с Blog; значение настраивает оператор |
| Контракт/версия Identity | Точные формы, ошибки, email verification/reset/session flows |
| Browser token storage или отдельный frontend BFF | Способ доставки Bearer; Blog BFF не предоставляет |
| Публичные URL страниц frontend | Например `/posts/:slug`; API не задаёт маршруты UI |
| Markdown renderer/санитизация и политика изображений | Безопасный вывод недоверенного текста |
| Анонимное хранилище и его scope | Стабильность anonymousVisitorId в одной установке |
| Отдельный профильный сервис, если нужен | Имена/аватары; без него UX использует ограниченный AuthorRef |
| Механизм выдачи тестовых ролей | Проверка Author/Moderator/Admin через защищённый Operator |

До уточнения адресов frontend можно проектировать и тестировать на mocks этого
контракта. Адреса, clientId, SLA и дополнительные серверные возможности нельзя
подменять догадками. Нагрузочные p95/p99 текущими документами не подтверждены;
нет оснований строить UX на гарантированном фиксированном времени ответа.

## 23. Приложение: граница внешнего Identity

Это **отдельный сервис**, не операции Blog API и не часть перечня 35 операций.
В репозитории Blog нет полного OpenAPI Identity. Ниже приведён только минимум,
который подтверждён существующим
[smoke-сценарием](../../scripts/validation/Invoke-IdentityIntegrationSmoke.ps1)
и [описанием проверки](../operations/deployment.md). Он помогает спроектировать
адаптер, но не заменяет актуальный контракт выбранного развертывания Identity.

| Внешняя операция | Данные, использованные smoke | Проверенный успех |
| --- | --- | --- |
| `POST {IDENTITY_BASE_URL}/api/v1/auth/register` | `{ email, password, clientId }` | 202 |
| `POST {IDENTITY_BASE_URL}/api/v1/auth/email/verify` | `{ token }` | 204 |
| `POST {IDENTITY_BASE_URL}/api/v1/auth/login` | `{ email, password, clientId }` | 200, token pair |
| `POST {IDENTITY_BASE_URL}/api/v1/auth/refresh` | `{ refreshToken }` | 200, новая token pair |
| `POST {IDENTITY_BASE_URL}/api/v1/auth/logout` | `{ refreshToken }` | 204 |
| `GET {IDENTITY_BASE_URL}/api/v1/users/me` | Bearer access JWT | 200, профиль в тесте; полная схема здесь не закреплена |
| `GET {IDENTITY_BASE_URL}/.well-known/jwks.json` | Без body | Публичный JWKS для проверки на backend |

Smoke проверяет в token pair наличие `accessToken`, `refreshToken` и
`tokenType="Bearer"`. Дополнительные поля, ограничения password/email, ошибки
регистрации, длительности и формы восстановления пароля не закрепляются этим
документом: их надо брать из полного контракта Identity. `clientId=blog` и
`audience=blog-api` использовались в одноразовой проверке, а не заданы для всех сред.

Подтверждённая семантика refresh: токен ротируется, повтор использованного
refresh token отклоняется 401 и может отозвать replacement family. Frontend должен
координировать refresh между конкурентными запросами/вкладками, не переиспользовать
старый refresh token и атомарно заменять пару по принятой архитектуре хранения.

Smoke извлекал email verification token из письма с fragment `#token=...`.
Это свойство проверенной конфигурации URL template, не универсальный маршрут
frontend. Форму verification URL согласовать с Identity/оператором.
JWKS загружает Blog backend; браузерному frontend не нужно самостоятельно
проверять подпись для получения локальных прав.

После login frontend вызывает **Blog** `/api/v1/me` именно с accessToken.
Профиль Identity `/users/me` не содержит/не определяет локальные роли Blog.
После logout Identity может отклонять `/users/me` сразу, а Blog ещё принимать
неистёкший JWT в пределах документированной задержки.

## 24. Приложение: точные ответы всех операций

Следующая таблица сверяется с нормативным OpenAPI. Общие протокольные ошибки
из раздела 17 также применимы. Все перечисленные ошибочные ответы используют
Problem; пустое тело успеха обозначено `—`.

<!-- operation-responses:start -->
| Метод и путь | operationId | Успех и DTO | Ошибки, явно перечисленные в OpenAPI |
| --- | --- | --- | --- |
| `GET /api/v1/me` | `getMe` | 200 Me | 401, 500, 503 |
| `GET /api/v1/publications` | `listPublications` | 200 PublicationPage | 400, 401, 500, 503 |
| `POST /api/v1/publications` | `createPublication` | 201 WorkspacePublication | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/publications/by-slug/{slug}` | `getPublicationBySlug` | 200 Publication | 400, 401, 404, 500, 503 |
| `GET /api/v1/publications/{id}` | `getPublication` | 200 Publication | 400, 401, 404, 500, 503 |
| `PUT /api/v1/publications/{id}` | `updatePublication` | 200 WorkspacePublication | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `DELETE /api/v1/publications/{id}` | `deletePublication` | 204 — | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `POST /api/v1/publications/{id}/publish` | `publishPublication` | 200 WorkspacePublication | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `POST /api/v1/publications/{id}/unpublish` | `unpublishPublication` | 200 WorkspacePublication | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/workspace/publications` | `listWorkspacePublications` | 200 WorkspacePage | 400, 401, 403, 500, 503 |
| `GET /api/v1/workspace/publications/{id}` | `getWorkspacePublication` | 200 WorkspacePublication | 400, 401, 403, 404, 500, 503 |
| `GET /api/v1/categories` | `listCategories` | 200 TaxonomyPage | 400, 401, 500, 503 |
| `POST /api/v1/categories` | `createCategory` | 201 Taxonomy | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/categories/{id}` | `getCategory` | 200 Taxonomy | 400, 401, 404, 500, 503 |
| `PUT /api/v1/categories/{id}` | `renameCategory` | 200 Taxonomy | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `DELETE /api/v1/categories/{id}` | `deleteCategory` | 204 — | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/tags` | `listTags` | 200 TaxonomyPage | 400, 401, 500, 503 |
| `POST /api/v1/tags` | `createTag` | 201 Taxonomy | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/tags/{id}` | `getTag` | 200 Taxonomy | 400, 401, 404, 500, 503 |
| `PUT /api/v1/tags/{id}` | `renameTag` | 200 Taxonomy | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `DELETE /api/v1/tags/{id}` | `deleteTag` | 204 — | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/publications/{id}/comments` | `listComments` | 200 CommentPage | 400, 401, 404, 500, 503 |
| `POST /api/v1/publications/{id}/comments` | `createComment` | 201 CommentEditContext | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/comments/{commentId}` | `getCommentEditContext` | 200 CommentEditContext | 400, 401, 403, 404, 500, 503 |
| `PUT /api/v1/comments/{commentId}` | `updateComment` | 200 CommentEditContext | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `DELETE /api/v1/comments/{commentId}` | `deleteComment` | 204 — | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/moderation/comments` | `listModerationComments` | 200 ModerationPage | 400, 401, 403, 404, 500, 503 |
| `POST /api/v1/comments/{commentId}/hide` | `hideComment` | 200 ModerationComment | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `POST /api/v1/comments/{commentId}/restore` | `restoreComment` | 200 ModerationComment | 400, 401, 403, 404, 409, 413, 415, 500, 503 |
| `GET /api/v1/publications/{id}/counters` | `getCounters` | 200 Counters | 400, 401, 404, 500, 503 |
| `PUT /api/v1/publications/{id}/like` | `setLike` | 200 Counters | 400, 401, 403, 404, 500, 503 |
| `DELETE /api/v1/publications/{id}/like` | `removeLike` | 200 Counters | 400, 401, 403, 404, 500, 503 |
| `POST /api/v1/publications/{id}/views` | `registerView` | 200 ViewResult | 400, 401, 404, 500, 503 |
| `GET /health/live` | `healthLive` | 200 Health | — |
| `GET /health/ready` | `healthReady` | 200 Health | 503 |
<!-- operation-responses:end -->
