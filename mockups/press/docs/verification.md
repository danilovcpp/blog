# Проверка прототипа

27 октября 2026. Проверялся локальный UI с fixtures, без настоящего Blog/Identity.

| Проверка | Результат |
| --- | --- |
| TypeScript strict + production build | PASS |
| Сборки `paper`, `forest`, `midnight` | PASS, отдельные статические каталоги |
| Pure consumer rules | 6/6 PASS, без skipped |
| Prettier source/test/config check | PASS |
| Exact dependency versions ↔ lockfile | Совпадают |
| Локальные ссылки документации | Существуют |
| Source scan fetch/axios/XMLHttpRequest | API-вызовов нет |

В браузере проверены:

- Desktop главная трёх тем: палитра, локальные фотографии, hero, навигация.
- Mobile 390 px через реальный iframe viewport: главная и форма входа.
- Mock login с выдуманными данными: переход к журналу с ролью Reader.
- Forgot password: нейтральное accepted-состояние без раскрытия существования email.
- Author editor: сохранение Draft, publish confirmation, успешная публикация,
  заблокированный после первой публикации slug и предупреждение Published edits.
- Browser back с несохранённым текстом: переход блокируется, «Остаться» сохраняет
  введённый текст; затем Save успешно завершает запись.
- Article: один mock view после открытия, like 128 → 129 и синхронное обновление
  кнопок; Hidden placeholder раскрывает сохранённые видимые ответы без hidden text/author.
- Admin taxonomy: попытка удалить связанную категорию оставляет запись и показывает
  объяснение `taxonomy_in_use`.
- Модерация: отдельный private список с retained Hidden text, фильтрами и диалогом hide.

Это набор ручных smoke-проверок ключевых journeys. Полный browser automation suite,
аудит WCAG с assistive technology и реальные backend integration tests не выполнялись.
Остальные состояния доступны на `/prototype` для предметного ревью.

## Скриншоты

- [Главная / Paper](previews/home.png).
- [Forest](previews/forest.png).
- [Midnight](previews/midnight.png).
- [Identity / login](previews/identity.png).
- [Мобильная компоновка](previews/mobile.png).
- [Редактор](previews/editor.png).
- [Обсуждение и placeholders](previews/discussion.png).
