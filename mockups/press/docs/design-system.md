# UI-архитектура и дизайн-система

## Слои реализации

```text
src/main.tsx                 React entry / router / mock provider / theme
src/App.tsx                  route guards / lazy page modules
src/themes/index.ts          build-time brand packages
src/components/ui.tsx        buttons, fields, alerts, modal, loading/empty/error
src/components/Layout.tsx    public, editorial, account shells + demo tools
src/pages/Journal.tsx        feed, collections, topics, about, not-found
src/pages/Article.tsx        safe Markdown, reading, discussion, private comment
src/pages/Workspace.tsx      author workspace and editor
src/pages/Moderation.tsx     moderation and taxonomy
src/pages/Identity.tsx       all customer account/auth flows
src/pages/Prototype.tsx      scenario atlas and real responsive iframe preview
src/mock/data.ts             deterministic publication/comment/account fixtures
src/mock/store.tsx           in-memory operations and role/state simulation
src/mock/rules.ts            pure consumer rules
src/styles.css              semantic tokens, component styles, responsive rules
```

Страницы загружаются отдельными chunks. API-библиотека, сервер, MSW и реальная
авторизация не подключены. Изоляция mock operations позволяет позднее заменить
адаптер, не превращая компоненты в места хранения HTTP-контракта.

Числа в рабочей области относятся к загруженному набору fixtures, а не к
несуществующему серверному total. Описания тематических коллекций — редакционный
контент frontend: API справочников возвращает только id/name/slug/version.

## Tokens

| Семантический token | Назначение |
| --- | --- |
| `--bg` | Основной фон, контрастная надпись primary button |
| `--surface` | Формы, карточки кабинета и диалоги |
| `--ink` | Основной текст |
| `--muted` | Вторичный текст и подписи |
| `--line` | Разделители, границы и структура |
| `--accent` | Primary CTA, активная навигация, взаимодействие |
| `--soft` | Мягкая акцентная поверхность |
| `--danger` | Необратимые действия и ошибки |
| `--serif` | Editorial headings / body reading / pull quotes |

Цвета компонентов выражены через семантику; error/warning поддерживают отдельные
тёмные значения. Изображения и их alt принадлежат brand package. Картинки локальные,
а системные шрифты включают кириллицу и не требуют font CDN.

## Типографика и ритм

- Editorial hero 46–66 px в зависимости от viewport; заголовок материала 39–57 px.
- Основная reading column — до 700 px, 18 px, межстрочный 1.85–1.9.
- Навигация/формы используют sans-serif; поясняющий текст и метаданные имеют
  более низкую иерархию. Снижение контраста не заменяет содержательное разделение.
- Базовый ритм отступов 4/8/12/16/24/32/48/64. Читаемый content width до 1280 px.
- Radius 5–9 px у controls/card images, 12 px у диалогов. Тени только для overlay/
  transient feedback. Избыточных градиентов и floating cards на каждом уровне нет.

## Адаптивные правила

| Ширина | Поведение |
| --- | --- |
| >1100 | Full editorial hero, три карточки, TOC, sidebars, editor inspector |
| 801–1100 | Более компактные hero/sidebars, сохраняется основная сетка |
| 561–800 | Две карточки, account/editorial navigation горизонтально, TOC убирается |
| ≤560 | Один столбец, mobile menu, hero сверху, stacked auth, inspector под текстом |

Sidebar превращается в nav strip, а не скрывает аккаунтные разделы.
На телефоне комментарии ограничивают визуальные отступы, сохраняя текст и
контекст ответа. Табличный справочник оставляет доступными name, slug и действия.

## Interaction rules

- Один primary CTA на логический блок; destructive action подтверждается отдельно.
- Длинные запросы блокируют соответствующую кнопку, сохраняя контекст страницы.
- Toast сообщает итог, inline alert сохраняет привязку к форме/ошибке.
- Empty, initial error и page append error — разные состояния.
- Modal удерживает фокус, закрывается Escape и возвращает фокус инициатору.
- Native form labels, autocomplete, видимый focus, skip link и reduced-motion
  включены. Это базовый accessibility слой; полноценный аудит с AT не заявляется.
- Markdown не включает raw HTML parser; обычный текст не выводится через HTML injection.
- UI роли и владельца проверяет перед показом действий, mock service также
  защищает изменения. Эти заглушки не являются production security boundary.

## Перед настоящей интеграцией

Нужно подключить typed Blog adapter и same-origin BFF для Identity, определить
real deployment URLs, обработку ProblemDetails, lossless int64 при необходимости,
серверную сессию и refresh coordination. Добавить реальные сетевые cancellation/
pagination/snapshot состояния и browser automation against integration fixtures.

Нельзя переиспользовать demo persona switch, `?demo=valid` и mock passwords в
production. Нельзя отправлять local `/images/` как настоящий API `coverUrl`:
fixture images — договорённость mock adapter, production API требует HTTPS.
