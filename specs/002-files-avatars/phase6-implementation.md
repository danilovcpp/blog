# Phase 6 — квота, очистка и восстановление

`GET /api/v1/media/allowance` читает использованные байты и активные резервы
в согласованном снимке PostgreSQL. В момент истечения upload резерв исчезает
из ответа независимо от worker. Удаление освобождает квоту в той же транзакции,
которая скрывает файл. Повтор DELETE сохраняет результат для владельца и Admin.

Worker ограничивает due jobs, expired uploads и reconciliation отдельными
пакетами. Переходы используют действующие блокировки владельца и поколения
leases. DeleteObject выполняется после фиксации claim и вне SQL-транзакции.
Даже завершённая работа оставляет tombstone для повторного удаления поздних
байтов. Первоначальный срок очистки отсчитывается от retirement или expiry.
Повторная очистка отслеживается по времени последней успешной сверки, поскольку
момент позднего S3 PUT может быть неизвестен API.

Maintenance блокирует мутации и новые claims. Readiness проверяет schema marker,
read grants, содержимое sentinels обоих private buckets, spool и native codec.
Liveness остаётся проверкой процесса. Операционные процедуры описаны в
[runbook](../../docs/operations/media-recovery.md).

## Code review и constitution 1.0.0

| Принцип | Результат ревью |
|---|---|
| I. Модульность | Media владеет состояниями и очисткой. Core содержит правила переходов и не ссылается на EF, ASP.NET или Garage SDK. |
| II. C# | Ввод-вывод асинхронный, фоновые циклы и сеть получают cancellation token. Release-сборка с анализаторами проходит без предупреждений. |
| III. Данные | Схема не меняется. Переиспользованы версионированные ограничения, короткие транзакции, owner locks и ограниченные SQL-выборки. SQL/EXPLAIN трёх очередей сохранены. |
| IV. API и безопасность | Quota DTO содержит только байты. Выбор владельца проверяет актуальный Admin, ошибки используют ProblemDetails. OpenAPI срез соответствует маршрутам фазы. |
| V. Тесты | Проверены гонки, сроки, отрицательные сценарии, реальные PostgreSQL/Garage, восстановление и бюджет контейнера. |
| VI. Эксплуатация | Liveness отделён от readiness. Probes не применяют DDL и не исправляют хранилище. Телеметрия не содержит содержимого, имён, ключей и секретов. |

Ревью и регрессия обнаружили несогласованный OpenAPI срез, тестовые хосты без
Garage, ошибочный ответ при отсутствии schema marker и лишнюю полноразмерную
копию изображения при ориентации TopLeft. Эти проблемы исправлены. Проверка
квоты также удерживает снимок при получении времени БД, а выборка reconciliation
отфильтровывает jobs, для которых ещё не наступил `not_before`.

## Результаты проверок

| Проверка | Команда или артефакт | Результат |
|---|---|---|
| Release и анализаторы | `dotnet build BlogEngine.slnx -c Release --no-restore -warnaserror` | 0 ошибок, 0 предупреждений |
| Формат | `dotnet format BlogEngine.slnx --no-restore --verify-no-changes` | PASS |
| Unit | `artifacts/validation/media/phase6/final-unit.trx` | 99/99 |
| Регрессия MVP и Media | `artifacts/validation/media/phase6/final-integration.trx` | 320/320, граничный профиль запущен отдельно |
| Затронутые сценарии после ревью | `artifacts/validation/media/phase6/last-touched.trx` | 53/53, включая растровый корпус, правила авторизации и fault cases |
| Конкурентность через validation script | `Invoke-MediaValidation.ps1 -Scenario Concurrency -NoBuild` | 31/31, в том числе 20 успешных из 100 равных резервов при 80 `quota_exceeded` |
| Контроль отказов | `artifacts/validation/media/phase6/extended-us4.trx` | 8/8, включая потерянный ответ после SQL commit, оборванный GET, отказ 507, поздний потоковый PUT и restart |
| SQL/EXPLAIN | `artifacts/validation/media/phase6/queue-plans.json` | Три параметризованных выбора по очередям с LIMIT и порядком разрешения совпадений |
| Граничный профиль очистки | `artifacts/validation/media/phase6/final-cleanup-profile.trx` и `cleanup-profile.json` | 2/2. 10 000 начальных объектов, из них 5 000 completed, 1 000 начальных expiries и по 1 000 новых записей. Обход ≤12h, drain ≤24h. 288 циклов, самый долгий 13,4 s. Десять закрытых исходников сохранили имена и SHA-256. |
| Память и spool | `Invoke-MediaResourceValidation.ps1` | 1 055 289 344 байта peak при лимите 1 073 741 824, четыре HTTP передачи по 50 MiB и decode 40 млн pixels. Нет OOM/restart при нагрузке. После отдельного принудительного kill spool восстановлен на прежнем томе. |
| Восстановление | `Invoke-MediaRecoveryValidation.ps1` и `artifacts/validation/media/recovery.md` | SQL upgrade от `AddViewDeduplication`, перенос SQL и полных Garage volumes, проверка SHA-256, ссылок, аватаров, квоты и прав. Повторное восстановление отменяет записи после recovery point. |

Стенд использует закреплённые PostgreSQL 18.6 и single-node Garage v2.5.0.
Проверка производственной топологии и фактической ёмкости очистки относится
к rollout gate T061. Запас референсного прогона памяти составил около 18 MiB.
Локальный граничный тест занял 19 минут отдельно от регрессии. При T060 нужно
учесть его длительность в бюджете CI и сохранить самостоятельный результат прогона.
