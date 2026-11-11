# Хранилище аватарок и файлов

Дата решения: 2026-11-11 (Asia/Omsk).
Статус: выбрано хранилище, подготовлены спецификация и технический план; интеграция и развёртывание
не выполнены.

## Требования и выбор

Пользователь выбрал собственное S3-совместимое хранилище с обязательными
условиями: open-source и бесплатное использование программного обеспечения.
Для Blog выбран **Garage** от Deuxfleurs, лицензия **AGPLv3**.
Платная подписка для самостоятельного использования не нужна; инфраструктура
и резервные копии обеспечиваются владельцем установки.

Garage ориентирован на небольшие и средние самостоятельные установки.
Он не требует внешних сервисов для своей работы. Разработчики указывают
минимум 1 GB RAM и 16 GB дискового пространства; это требования самого
хранилища, а не бюджет всего стека Blog/PostgreSQL.

В опубликованной матрице совместимости указаны PutObject/GetObject/HeadObject,
DeleteObject, presigned URLs, multipart upload и CORS. Эти операции покрывают
предполагаемые сценарии аватарок, изображений и вложений Blog. Совместимость
выбранной версии с .NET S3-клиентом ещё предстоит подтвердить интеграционными
проверками; полная совместимость с AWS S3 не предполагается. В частности,
матрица отмечает отсутствие bucket versioning и AWS IAM/bucket policies;
Garage использует собственные права на уровне ключа и bucket.

## Принятое проектное решение

Планирование Phase0/1 завершено в
[002-files-avatars/plan.md](../../specs/002-files-avatars/plan.md).
Это проект реализации, не подтверждение работающей интеграции.

- Media внутри Blog владеет файлами, квотами, аватаром и registrations;
  метаданные в PostgreSQL, bytes в двух закрытых buckets Garage.
- Передача идёт через Blog API с bounded disk spool, проверкой длины/SHA-256
  и повторной авторизацией перед выдачей. Presigned URLs/browser CORS в первом
  этапе не используются. Старые URL аватаров перестают разрешаться сразу.
- Garage v2.5.0 и exact SDK/image dependency versions выбраны в research;
  manifest digest закрепляется до acceptance реализации. Совместимость
  подтверждается на настоящем Garage/PostgreSQL, не только mock-клиентом.
- Production topology: три storage nodes в трёх failure domains, replication3;
  single-node только dev/integration. Backup — согласованный recovery set
  SQL+всех Garage volumes в окне обслуживания с остановленными writers.
- HTTP/OpenAPI/data model, operational limits и restore validation описаны
  в артефактах функции. Production UI и подключение прототипа по Q1
  выполняются отдельным этапом. Следующая команда — `$speckit-tasks`.

Пользовательские требования — [spec.md](../../specs/002-files-avatars/spec.md),
история ограничений — [source-constraints.md](../../specs/002-files-avatars/references/source-constraints.md).
Ранее предложенные direct/presigned delivery и открытые buckets не являются
принятым вариантом первого этапа. Реализованные контракты MVP ещё не менялись.

## Проверенные источники

- [Официальное зеркало исходников и лицензия](https://github.com/deuxfleurs-org/garage).
- [Цели и требования Garage](https://garagehq.deuxfleurs.fr/).
- [Возможности](https://garagehq.deuxfleurs.fr/documentation/reference-manual/features/).
- [Матрица S3-совместимости](https://garagehq.deuxfleurs.fr/documentation/reference-manual/s3-compatibility/).
- [Quick start и ограничения одного узла](https://garagehq.deuxfleurs.fr/documentation/quick-start/).
