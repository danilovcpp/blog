# Карта экранов и сценариев

Проверять из `/prototype`: ссылки устанавливают подходящую demo-персону.
Коллекции, карточки, диалоги и формы используют один набор in-memory fixtures.

| Экран/route | Роль | Основные сценарии |
| --- | --- | --- |
| `/` | Любая | Recent/popular, сетка/список, OR-category/AND-tags, load more, empty/loading/offline/503, page append error |
| `/topics` | Любая | Темы и теги, переход в коллекцию |
| `/category/:slug` | Любая | Лента категории и дополнительные фильтры |
| `/tag/:slug` | Любая | Лента тега и дополнительные фильтры |
| `/posts/:slug` | Любая | Чтение, derived TOC/time, copy URL, like gate, discussion, replies и placeholding |
| `/comments/:id/manage` | Owner/Admin | Private context, own Hidden delete, Admin Hidden edit, Deleted state |
| `/about` | Любая | Редакционный манифест и переход к журналу |
| `/workspace` | Author/Admin | Свои/все материалы, Draft/Published, empty, delete confirmation |
| `/workspace/new` | Author/Admin | Новый Draft, валидация, slug conflict, preview, save/publish |
| `/workspace/:id/edit` | Owner+Author/Admin | Полная форма, inspector, immutable slug, save/unpublish/delete, dirty leave, concurrency dialog |
| `/moderation` | Moderator/Admin | Publication/state filters, hide/restore, retained Hidden text, conflict, Admin edit/delete |
| `/admin/taxonomy` | Admin | Категории/теги, create/rename/delete, unique slug, immutable slug, taxonomy_in_use |
| `/auth/login` | Гость | Login, reveal password, generic invalid credentials, 429 cooldown, 503/offline, returnTo |
| `/auth/register` | Гость | Email/name/new password/confirmation, structural policy, generic accepted |
| `/auth/check-email` | Гость | Нейтральное письмо-состояние, resend, return to login |
| `/auth/verify-email` | Гость | Пригодная/непригодная ссылка, verify success, resend |
| `/auth/resend-verification` | Гость | Email, generic accepted, resend cooldown |
| `/auth/forgot-password` | Гость | Email, generic accepted, cooldown, back to login |
| `/auth/reset-password` | Гость | Link state, new password, confirmation, success без auto-login |
| `/auth/accept-invitation` | Гость | Link state, new password, confirmation, activation success |
| `/auth/session-expired` | Гость | Session expired/revoked, повторный вход |
| `/auth/deactivated` | Гость | Терминальная деактивация, без restore/PII-erasure обещаний |
| `/account/profile` | Reader+ | Own displayName edit/clear, read-only verified email, local Blog roles |
| `/account/security` | Reader+ | Current/new/confirm, password change, all-sessions consequence, login |
| `/account/sessions` | Reader+ | Current/other active sessions, revoke one/all, confirmation, logout current |
| `/account/deactivate` | Reader+ | Consequences, password, acknowledgement, final confirmation, terminal state |
| `*` | Любая | 404/unavailable без раскрытия Draft |
| `/prototype` | Reviewer | Route atlas, role/state scenarios, reset fixtures |
| `/prototype/responsive` | Reviewer | Реальная responsive iframe-компоновка 390/768 px |

## Компактный walkthrough

1. Главная → категория → материал. Проверить, что посетитель не вынужден входить.
2. «Прототип» → Читатель. Like, комментарий, reply, own edit/delete.
3. Раскрыть Hidden и Deleted placeholders: ответы доступны, скрытые текст/автор не раскрыты.
4. Карта → Author → черновик. Изменить текст → сохранить → preview → publish.
5. Попробовать менять slug после publish/unpublish: поле заблокировано.
6. Карта → конфликт сохранения. Нажать Save, сравнить версии, сохранить собственный ввод.
7. Moderator → hide/restore. Сверить структуру обсуждения; Deleted восстановить нельзя.
8. Admin → taxonomy. Создать неиспользуемый тег → rename → delete. Попробовать delete
   связанной категории: получить объяснение taxonomy_in_use.
9. Login invalid/429 через atlas. Recovery/reset/verify/invitation имеют отдельные
   valid/invalid deep-link состояния; письма сообщают только accepted processing.
10. Profile → change password → session-ended/login. Sessions → revoke one или all.
11. Deactivate → password/acknowledgement → confirmation → deactivated.
12. Повторить читательский и auth путь во viewport 390 px.

## Что специально не нарисовано как рабочая функция

Полнотекстовый поиск, подписки, закладки, загрузка файлов, отложенная публикация,
редакционная очередь approve, profile любого автора, HTTP role management,
social/MFA/passkeys, change email, account erasure и restore deactivate.

## Ограничения прототипа

Это локальная UI-модель: mock delays не проверяют HTTP latency, JWKS, реальные
сессии, доставку email, denylist паролей и транзакционные гонки. Пагинация
демонстрируется срезами fixtures, поэтому нет реального server offset drift.
Данные вкладки не являются durable draft storage. Подтверждение выхода из
редактора работает у кнопки возврата и SPA-переходов, включая browser back:
используется Data Router blocker. Перезагрузка/закрытие защищены beforeunload.
