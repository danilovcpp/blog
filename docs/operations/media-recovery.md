# Media: согласованное восстановление

Процедура относится к PostgreSQL 18.6 и Garage v2.5.0 с digest из
`compose.yaml` и `ops/media/production-inventory.example.yaml`. Проверка
single-node fixtures подтверждает перенос SQL и полных остановленных volumes;
production требует inventory трёх независимых failure domains и отдельной
репетиции потери узла. Не подменяйте её тремя контейнерами на одном сервере.

## Provisioning и проверка перед копией

1. Примените reviewed additive migrations и `ops/database/permissions.sql`
   отдельным DDL principal. API получает только runtime grants; Operator не
   получает Media grants. Startup не создаёт schema/buckets и не исправляет права.
2. Из защищённой operator-среды выполните `ops/media/Provision-MediaStorage.ps1`.
   Admin/provisioning credentials доступны только provisioning provider.
   Runtime key ограничен двумя private buckets, без public website/createBucket/
   owner. Sentinel `_blog/health-v1` в каждом bucket имеет известные length/hash.
3. Проверьте readiness, исходные файлы и PNG, роли и актуальные ссылки. Запишите
   recoverySetId, revision, UTC quiesce time, exact image manifests, migration ID,
   schema/grants, node IDs/layout и соответствие каждого node двум volumes.

## Окно обслуживания и recovery set

1. На ingress закройте mutations и доступ сторонних writers, включая Operator.
   На всех API включите `Media:MaintenanceMode=true`. Новые processing/cleanup
   claims прекращаются, readiness становится отрицательной, liveness сохраняется.
   Уже начатые операции дренируются с их ограниченными таймаутами.
2. Штатно остановите все API и Operator, включая GET. Одной блокировки POST/PUT/
   DELETE недостаточно: GET может создать локального пользователя или
   empty avatar. Подтвердите отсутствие активных запросов, workers и DB writer
   sessions; закройте прямой доступ прежних credentials. Исключите другие tools.
3. Пока Garage healthy/synchronized, получите из SQL полный manifest действующих
   Source/representations: bucket, key, file/representation ID, length, SHA-256.
   Потоковым GET проверьте **каждый** действующий объект. Missing/corrupt object,
   pending repair или несовпадение manifest блокирует согласованную копию.
4. Выполните `pg_dump --format=custom` через backup credential и сохраните
   globals/roles/grants в защищённом наборе. Не передавайте пароль в аргументах.
   Используйте provider, `.pgpass` с ограниченными правами или secret mount.
5. Штатно остановите **все** Garage nodes. Скопируйте полностью metadata и data
   каждого остановленного узла, его config, layout/node identity и необходимые
   RPC/runtime/provisioning keys. Копирование live metadata DB запрещено.
   Не объединяйте volumes из разных recoverySetId.
6. Создайте manifest со списком всех частей, SHA-256, версиями, node/volume mapping
   и recoverySetId. Шифруйте весь recovery set; ключи шифрования храните отдельно
   через защищённый recovery provider. Применяйте retention **7 daily / 4 weekly**,
   проверяйте checksums и фактический возраст последней восстановленной точки.
   Измеряйте время backup/restore; численные RPO/RTO этим этапом не обещаны.
7. Запустите Garage и совместимый API при закрытой внешней записи, снимите
   maintenance для readiness/smoke. Откройте запись только после успешных проверок.

Spool в backup не входит: это восстанавливаемые временные bytes, а не единственная
копия Ready content. У новой replica должен быть чистый эксклюзивный private volume.

## Restore в изолированный стенд

1. Проверьте recoverySetId, exact pins и checksums всех частей до подключения
   данных. Подготовьте чистую SQL БД, все новые metadata/data volumes и тот же
   node/layout mapping. Восстановите защищённые config/keys и roles/grants.
2. При остановленной записи восстановите SQL dump и **весь** Garage set.
   Запустите Garage и дождитесь healthy/synchronized без потерянных объектов.
3. При гарантированно остановленных writers сбросьте `lease_until` в
   `media.uploads`, `media.objects`, `media.cleanup_jobs` в одной SQL транзакции.
   Не уменьшайте и не обнуляйте generations. Сохраните состояния и immutable
   completion results. Processing/cleanup возобновятся fenced claims; expiry
   оценивается по текущему DB clock, а не по времени backup.
4. Запустите совместимый API с новым spool и закрытым ingress записи. Проверьте
   readiness/runtime grants, все manifest lengths/SHA-256, исходные filenames,
   publication links и доступ к Draft/Published, avatar/version и обе PNG,
   quota used/reserved, отсутствие оживших abandoned uploads, чужой ресурс 404,
   Hidden/Deleted без author и прежние разрешения Operator.
5. После успешного consumer smoke и проверки очередей откройте запись.
   При ошибке сохраните закрытую запись и evidence; не исправляйте метаданные
   произвольно и не переключайте credential на непроверенную копию.

## Upgrade, rollback и потеря узла

Перед новой версией Garage изучите её migration guide/release notes, закрепите
новый digest, создайте проверенный recovery set и проведите rehearsal на копии
данных. После записи нового формата rollback выполняется восстановлением всего
прежнего набора; смена image сама по себе не восстанавливает формат. Проверка
текущего релиза не является проверкой произвольной будущей версии.

Production Down запрещён. Совместимый backend можно вернуть с additive schema,
сохранив закрытую запись до проверки. После первой managed связи прежний writer,
не учитывающий usages, возвращать нельзя. Для возврата такого writer нужен
проверенный полный recovery set **после additive migration, до первой managed
записи**. Оператор явно оценивает записи после recovery point и принимает решение
об их потере/отдельном переносе; это не автоматическое действие скрипта.

До запуска нового backend закройте ingress прежних writers, дренируйте и
остановите все их экземпляры, проверьте отсутствие DB sessions. Отзовите их
write credentials и отрицательным probe через новое соединение подтвердите
невозможность записи. Новые credentials недоступны прежним экземплярам. Затем
запускайте совместимую запись и выполняйте smoke. Нельзя сначала создать managed
связь, а затем останавливать старые writers.

Репетиция node loss выполняется на фактических трёх domains: выведите один узел,
проверьте чтение/запись и восстановление replication/layout, затем повторите
manifest/grants/hash проверки. Single-node backup test этого не подтверждает.

## Воспроизводимая локальная проверка

```powershell
./scripts/validation/Invoke-MediaValidation.ps1 -Scenario Cleanup
./scripts/validation/Invoke-MediaRecoveryValidation.ps1 `
  -RecoverySetDirectory artifacts/validation/media/rehearsal-set `
  -OutputDirectory artifacts/validation/media/rehearsal
```

Пустой каталог создаёт изолированный зашифрованный fixture recovery set и дважды
восстанавливает его в новые SQL БД и volumes, проверяя rollback к той же точке
после новых записей. Существующий каталог восстанавливается только с 32-byte key
из `BLOG_MEDIA_RECOVERY_KEY_BASE64`; скрипт проверяет manifest/checksums и exact
pins. Не передавайте этот ключ аргументом и не сохраняйте в Git/отчётах. Если
provider не задан для нового набора, временный ключ уничтожается после rehearsal;
такой disposable набор впоследствии не восстановить. Скрипт принимает только
fixture-owned роли и не предназначен для автоматического production restore.
Результаты — TRX и `artifacts/validation/media/recovery.md`; пустой/пропущенный
suite, manifest mismatch и недоступная зависимость дают ненулевой результат.
