# Research: файлы и аватары

Дата: 2026-11-11. Основание: [spec.md](spec.md),
[исходные ограничения](references/source-constraints.md), конституция 1.0.0.
Это решения планирования; совместимость и эксплуатационные гарантии ещё
предстоит доказать при реализации.

## 1. Границы и зависимости

- **Decision:** добавить Media в существующие Core/Infrastructure/Api без
  отдельного сервиса. Media владеет файлами, загрузками, квотами, аватаром,
  регистрациями использования файлов и заданиями очистки. Accounts сохраняет
  идентичность и роли. Publishing определяет семантику публикации и вызывает
  Media.Contracts для замены регистраций в своей транзакции.
- **Rationale:** один PostgreSQL позволяет атомарно менять статью и ссылки;
  Garage остаётся внешней границей. Граф Core: Media → Accounts.Contracts;
  Publishing → Accounts.Contracts + Media.Contracts; ReadModels → существующие
  контракты + Media.Contracts. Media не обращается обратно к Publishing.
  Проверка доступности статьи и файла компонуется в ReadModels/API.
- **Alternatives considered:** отдельный Media service требует распределённой
  согласованности; аватар в Accounts с вызовами Media создаёт цикл;
  Media → Publishing для проверки использования также создаёт цикл.
- **Local evidence:** `src/BlogEngine.Core/Accounts/Domain/LocalUser.cs`,
  `src/BlogEngine.Core/Publishing/Contracts/IPublicationWriteScope.cs`,
  `tests/BlogEngine.UnitTests/Architecture/DependencyTests.cs`.

## 2. Закреплённые версии

- **Decision:** сохранить SDK 10.0.402, net10.0/C#14, ASP.NET Core/EF Core
  10.0.13, Npgsql/provider 10.0.3 и PostgreSQL 18.6 с существующим digest
  из `compose.yaml` и `Fixtures/PostgresImage.cs`.
  Новые зависимости: AWSSDK.S3 **4.0.104.12**, AWSSDK.Core **4.0.103**,
  SkiaSharp и SkiaSharp.NativeAssets.Linux.NoDependencies **4.155.0**.
  Garage — **dxflrs/garage:v2.5.0**. Все NuGet versions задаются в
  `Directory.Packages.props` и lock files. До первого запуска acceptance
  image tag разрешается в manifest digest, который одинаково закрепляется
  в Compose, production inventory и тестовой фикстуре. Выдуманный digest
  не подставляется; это обязательный deployment gate реализации.
- **Rationale:** существующий стек менять не требуется; SkiaSharp имеет MIT,
  не требует платной лицензии. Garage выбран пользователем и имеет AGPLv3.
- **Alternatives considered:** ImageSharp требует отдельной оценки лицензии;
  floating/latest images и диапазоны NuGet запрещают воспроизводимость.
- **Sources:** [Garage releases](https://garagehq.deuxfleurs.fr/_releases.html),
  [AWSSDK.S3](https://www.nuget.org/packages/AWSSDK.S3/4.0.104.12),
  [AWSSDK.Core](https://www.nuget.org/packages/AWSSDK.Core/4.0.103),
  [SkiaSharp](https://www.nuget.org/packages/SkiaSharp/4.155.0),
  [Linux native assets](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Linux.NoDependencies/4.155.0).

## 3. Доставка, целостность и кеши

- **Decision:** все bucket закрытые; клиент работает только с Blog API.
  Raw PUT передаётся в ограниченный временный файл, считаются длина,
  SHA-256 и MD5, затем байты сохраняются в Garage. Скачать можно только
  через Blog: сначала spool объекта и проверка длины/SHA-256, затем повторная
  проверка текущих прав непосредственно перед HTTP-заголовками и потоковая
  выдача локального файла. Данные целиком в RAM не загружаются.
  Publication media: `Cache-Control: no-store`; avatar URL содержит версию,
  но также выдаётся с `no-store` в первом этапе. Старый avatar URL после смены
  сразу перестаёт разрешаться. Это укладывается в допустимые 60/300 секунд.
- **Rationale:** FR-030 требует обнаруживать повреждение до успешного полного
  ответа; проверка только после отправки 200 недостаточна. Повторная проверка
  после spool исключает устаревшее разрешение при медленном Garage GET.
  Разрешение действует для уже начатого ответа; Range/retry — новый запрос
  с новой авторизацией. На первом этапе Range не поддерживается и игнорируется
  с полным ответом 200; кеширующие validators/304 не выдаются.
- **Alternatives considered:** presigned GET усложняет целостность, доступ и
  кеширование; direct PUT — проверку фактических байтов/повторной записи.
  Размер максимум 50 MiB позволяет обойтись без multipart и browser CORS.

## 4. Garage SDK boundary

- **Decision:** ServiceURL из доверенной Options, AuthenticationRegion=garage,
  ForcePathStyle=true, SigV4; RequestChecksumCalculation и
  ResponseChecksumValidation — WHEN_REQUIRED. Single-part PUT известной длины:
  UseChunkEncoding=false, Content-MD5 из фактических байтов, payload signing
  включён. Каноническая целостность приложения — SHA-256; ETag не заменяет её.
  Два закрытых bucket: originals (источники/вложения) и renditions
  (подготовленные изображения). Оба доступны только runtime-ключу Blog;
  отдельный provisioning key создаёт buckets/права и не передаётся API.
- **Rationale:** явная конфигурация устраняет предположение о полном AWS S3 API.
- **Alternatives considered:** AWS IAM/bucket versioning не опора решения;
  Garage использует собственные права bucket/key. Автоматическое создание
  buckets или расширение прав runtime при старте не допускается.
- **Sources:** [S3 compatibility](https://garagehq.deuxfleurs.fr/documentation/reference-manual/s3-compatibility/),
  [AmazonS3Config](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TS3Config.html),
  [PutObjectRequest](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutObjectRequest.html),
  [Garage Content-MD5 implementation](https://github.com/deuxfleurs-org/garage/blob/v2.5.0/src/api/s3/put.rs).

## 5. Растровая обработка

- **Decision:** SkiaSharp codec проверяет JPEG/PNG/WebP, заголовок, размеры,
  число кадров и полный decode. До bitmap allocation проверяются 40 млн
  пикселей/16384 на сторону. APNG и animated WebP отклоняются, включая файлы,
  у которых decoder показывает только первый кадр: отдельная проверка
  контейнера обязательна. Ориентация применяется до resize/crop. Производные
  кодируются заново в **PNG**, без переноса исходных metadata, с alpha.
  Аватар 256/512 — квадрат с центральной обрезкой и допустимым увеличением;
  publication image — fit ≤2560, без увеличения.
- **Rationale:** PNG сохраняет прозрачность и не требует выбора потерь качества.
  Старые source bytes publication image сохраняются отдельно, avatar source
  после успешного назначения уходит в очистку.
- **Alternatives considered:** WebP уменьшает размер, но добавляет настройку
  качества; исходный формат не выдаётся читателям как подготовленное изображение.
- **Source:** [SKCodec](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcodec?view=skiasharp).

## 6. Согласованность без транзакции с Garage

- **Decision:** POST создаёт Upload с fileId и резервом; PUT фиксирует байты
  в staging object; POST complete ставит persistent processing job.
  Worker проверяет/создаёт объекты вне DB-транзакции, затем короткая
  транзакция переводит файл в Ready и, для avatar, меняет текущую ссылку/версию.
  Все внешние записи заранее имеют ObjectRecord и уникальный attempt key.
  Leases с fencing generation запрещают устаревшему worker вернуть файл
  к жизни после expiry/delete. Повторы complete возвращают сохранённый
  результат, а не выполняют назначение аватара повторно.
- **Rationale:** общий commit PostgreSQL+S3 отсутствует; tracked objects и
  persistent cleanup позволяют восстановиться в каждой точке обрыва.
- **Alternatives considered:** держать DB lock во время S3, безусловно
  перезаписывать один ключ или удалять сироты только bucket listing — ненадёжно.

## 7. Квоты, гонки и HTTP-совместимость

- **Decision:** owner allowance row сериализует reserve/ready/delete/avatar
  и изменение registrations. Expired reserves исключаются по DB time даже
  до worker cleanup. Порядок блокировок: publication (если нужна) → allowance
  ownerId → avatar state → files по UUID → uploads/jobs. X-Resource-Version
  остаётся UUID; пустой avatar state также имеет постоянную версию.
  Командный результат создаётся в своей транзакции, без дочитывания после commit.
  Составные новые чтения используют один SQL snapshot либо короткий read-only
  REPEATABLE READ; один batch авторов на ограниченный набор результатов.
- **Rationale:** существующие F01/F05/F08 затрагивают новые пути. Для binary
  endpoints нужна отдельная политика middleware/Accept/body limits, поскольку
  текущий RequestContractValidation буферизует JSON и ограничивает его 8 MiB.
- **Alternatives considered:** глобально повысить JSON limit, прибавлять резерв
  без блокировки или считать только физически очищенные uploads — некорректно.
  Строгие старые схемы additionalProperties:false требуют явного согласованного
  обновления потребителей v1 и нормативного контракта до включения функции.
  Старый MVP YAML сохраняется как исторический baseline; новый aggregate
  включает все прежние операции и дополнения, а runtime schema проверяется
  до документального overlay (устранение F06 в этом пути).

## 8. Топология, backup и rollback

- **Decision:** production — три Garage node в трёх независимых failure domains,
  replication_factor=3, consistency_mode=consistent, постоянные metadata/data
  volumes, закрытые RPC/Admin. Один node допустим только для dev/integration.
  Production activation требует реального inventory трёх domains, а не трёх
  контейнеров одного VPS. Если инфраструктуры нет, production release блокируется.
  Backup первой версии — окно обслуживания: закрыть все mutations/Operator,
  остановить workers, дренировать in-flight, проверить здоровый синхронный
  кластер; pg_dump; остановить все Garage nodes; скопировать все data/metadata
  volumes, конфигурацию/layout и защищённые ключи в один recovery set.
- **Rationale:** после quiesce состояние не меняется и SQL/объекты относятся
  к одной логической точке. Garage snapshots одной metadata DB не заменяют
  согласованную копию Blog+объектов. Подробный runbook — в contracts/operations.md.
- **Alternatives considered:** один VPS не даёт HA; произвольная online-копия
  файлов не доказывает согласованность. После изменения формата Garage rollback
  выполняется восстановлением всего recovery set; возврата image недостаточно.
- **Sources:** [Garage configuration](https://garagehq.deuxfleurs.fr/documentation/reference-manual/configuration/),
  [failure domains](https://garagehq.deuxfleurs.fr/documentation/operations/recovering/),
  [upgrades](https://garagehq.deuxfleurs.fr/documentation/operations/upgrading/).

## Итог Phase 0

Все неизвестные Technical Context разрешены проектными решениями.
Непроверенные runtime-свойства перечислены как обязательные gates реализации,
а не как успешные испытания. Неизвестных продуктовых решений, требующих нового
сеанса clarify, нет. Конституционные исключения не запрашиваются.
