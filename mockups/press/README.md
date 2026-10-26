# Слой — UI/UX prototype

Локальный React-прототип Blog Engine. Все серверные операции заменены in-memory
заглушками; настоящие аккаунты, JWT, письма и backend-запросы отсутствуют.
Фотографии поставляются локально. Изменения данных сбрасываются при перезагрузке.

## Запуск

Нужен Node.js 22.12+ (проверено на 22.16) и npm.

```powershell
cd D:\Space\Personal\blog\mockup
npm ci
npm run dev
```

Открыть `http://127.0.0.1:5173/`. Карта всех экранов и состояний:
`http://127.0.0.1:5173/prototype`.

Кнопка **«Прототип»** справа внизу позволяет выбрать тестовую роль и состояние:
loading, empty, offline, 503, конфликт version, 429, неверные данные/ссылка,
ошибка следующей страницы. Это отдельный инструмент ревью, не функция назначения ролей.
Ссылки на карте автоматически выставляют подходящую роль и сценарий.

Мобильная проверка: `/prototype/responsive` — iframe с настоящим viewport
390 или 768 px. Внутри работают обычные media queries и тот же React UI.
Для аккаунтных сценариев выберите в iframe «Все роли и сценарии» и нужный экран.

## Темы до сборки

Темы не переключаются пользователем во время работы. Brand package выбирается
переменной окружения перед запуском или сборкой:

```powershell
$env:VITE_THEME = 'forest' # paper | forest | midnight
npm run build
npm run preview
```

Без переменной используется `paper`. После проверки удалите переменную из своей
shell-сессии при необходимости: `Remove-Item Env:VITE_THEME`.
Можно скопировать `.env.example` в `.env.local` и задать значение там.

| Тема | Визуальный характер |
| --- | --- |
| `paper` | Тёплая бумага, шалфей, натуральные интерьерные обложки |
| `forest` | Лесная палитра, природные ландшафты и горы |
| `midnight` | Глубокий тёмный фон, светлый акцент, море и вечернее чтение |

Цвета, изображения и alt-тексты: `src/themes/index.ts`. Семантические CSS-токены
применяются ко всем страницам, формам и диалогам. Компоненты не зависят от темы.
Новый brand package добавляется там же; список допустимых имён — в `vite.config.ts`.
Источники фото: [public/images/sources.md](public/images/sources.md).

## Что можно пройти

- Журнал, recent/popular, категории/теги, комбинированные фильтры OR/AND,
  сетка/список, подгрузка и частичная ошибка следующей страницы.
- Материал, локальное оглавление/reading time, лайк, явный mock-учёт просмотра,
  обсуждение, ответы, редактирование и удаление собственных комментариев,
  Hidden/Deleted placeholders с сохранёнными ответами.
- Редакция, Draft/Published, создание/редактирование, Markdown preview, параметры
  публикации, подтверждение publish/unpublish/delete, защита slug, конфликт версий.
- Модерация Visible/Hidden/Deleted с контекстом публикации и hide/restore.
  Admin дополнительно редактирует/удаляет чужой текст.
- Admin-справочники: create, rename, delete, slug conflict, taxonomy_in_use.
- Identity: login, register, check email, verify/resend, forgot/reset password,
  invitation acceptance, session expired, profile, password change, active
  sessions, revoke one/all, terminal deactivation и итоговый экран.

Login в обычном mock-режиме принимает любой корректно оформленный email и
непустой выдуманный пароль. Новые пароли проверяются по структурным ограничениям
Identity. Не вводите настоящие пароли: для ревью достаточно выдуманной фразы.
Прототип не моделирует реальную denylist, подпись JWT, SMTP или сетевой commit.

Email-сценарии не раскрывают существование аккаунта и не обещают доставку письма.
`?demo=valid` на reset/verify/invitation — явная заглушка пригодной ссылки.
В mockup поддержан разбор `#token=...` с немедленным удалением fragment из адреса;
токен нигде не сохраняется. Для production браузерной интеграции потребуется
отдельный BFF: настоящие refresh tokens в React storage не допускаются.

## Проверки

```powershell
npm test
npm run build
npm run format
```

`npm test` проверяет правила OR/AND, структурные комментарии, Unicode/slug,
password/NFKC, фиксированное окно просмотров и конфликт edit version.
Node 22.16 может сообщать о экспериментальном type stripping для test runner.
Это тесты потребительских правил, не подтверждение подключения к Identity/Blog.

Сборка выполняет строгую TypeScript-проверку и Vite build с разделением страниц
на отдельные chunks. Версии пакетов закреплены, `package-lock.json` включён.
При статическом хостинге нужен SPA fallback на `index.html` для глубоких ссылок.

## Материалы для следующего этапа

- [Исследование и дизайн-решения](docs/research.md).
- [Архитектура интерфейса и темы](docs/design-system.md).
- [Карта экранов и покрытие сценариев](docs/scenarios.md).
- [Результаты проверок и скриншоты](docs/verification.md).
- [Спецификация API Blog](../docs/api/frontend-api-spec.md).
- [Полный контракт Identity 1.1.0](../specs/001-blog-engine-mvp/references/Identity_Integration_Spec.md).

Дизайн избегает неподдержанных возможностей: search, upload, subscriptions,
bookmarks, публичные профили, выдача ролей через UI, social login/MFA,
изменение email и восстановление деактивированного аккаунта.
