# Развёртывание одной установки

Одна установка обслуживает один проект и один доверенный issuer/audience/JWKS.
PostgreSQL 18.6: UTF8, timezone UTC, collation C, без расширений. API, Operator
и миграционный процесс используют разные credentials. API/Operator не применяют
миграции при старте и не получают DDL, ownership либо superuser.

## Образы Linux x64

Из корня репозитория, с доступом к NuGet и MCR:

```powershell
docker build --platform linux/amd64 -f src/BlogEngine.Api/Dockerfile -t blog-engine-api:reviewed .
docker build --platform linux/amd64 -f src/BlogEngine.Operator/Dockerfile -t blog-engine-operator:reviewed .
```

SDK 10.0.402 и runtimes 10.0.13 закреплены реальными amd64 digests MCR;
зависимости восстанавливаются в locked mode. Hosts запускаются под APP_UID,
API слушает 8080. Operator — одноразовый console job в защищённой среде оператора,
без HTTP listener. Не публикуйте Operator как веб-сервис. Для обновления образов
повторно проверьте digest, locked restore, build, анализаторы и все suites.

## Конфигурация и ingress

Передайте секреты через защищённый configuration provider среды; для env provider
используйте ConnectionStrings__Blog и разделители __ в именах options.
API требует Identity__Issuer, Identity__Audience, Identity__JwksUrl (HTTPS),
AnonymousVisitors__ActiveKeyId и AnonymousVisitors__Keys__ID, AllowedHosts,
а также Api__AllowedOrigins__0 и следующие явно разрешённые origins при необходимости.
Operator требует только Identity__Issuer и собственный ConnectionStrings__Blog.
Не включайте значения секретов в docker build args, layers, отчёты или логи.

Все реплики API получают один visitor key ring. Ротация выполняется по
[anonymous-visitors.md](anonymous-visitors.md), сначала распространение ключа,
потом смена active key; удаление старого ключа инвалидирует старые visitor tokens.
TLS завершается на доверенном ingress. Ограничьте прямой доступ к контейнерам,
настройте body envelope не меньше контрактных 8 MiB с безопасным 413 на внешней
границе. `/health/live` — process liveness, `/health/ready` — DB/JWKS readiness;
readiness доступен внутренней сети. В Production OpenAPI/Swagger выключены.
Документация потребителя: нормативные openapi.yaml и operations.md.

## Отдельный миграционный шаг и rollout

1. На одноразовой БД выполните `scripts/validation/Invoke-MigrationValidation.ps1`
   и проверьте `artifacts/validation/migrations/reviewed-chain.sql`. Проверяются
   пустая схема, каждый реальный baseline, сохранение данных, idempotent replay,
   ограничения и права. InitialAccounts не имеет выдуманной предыдущей версии.
2. Снимите backup production и проверьте восстановление в изолированную БД.
   Сохраните revision, применённую migration, schema checksum, image digests и
   подтверждённый restore. Потеря backup либо непроверенный restore блокируют rollout.
3. Оцените SQL до применения. Текущие Up добавляют таблицы/индексы/ограничения;
   разрушающего преобразования существующих данных нет. DDL требует locks,
   а CREATE INDEX выполняется в migration transaction. Длительность зависит от
   реальных данных; измерьте на копии и согласуйте окно. Не запускайте два migrators.
   Настройте ограниченные lock_timeout/statement_timeout миграционной сессии.
4. Выполните reviewed idempotent SQL или `dotnet ef database update` отдельной
   migration credential. Не передавайте её API/Operator. Скрипт permissions.sql
   применяет оператор БД с проверенными именами owner/migration/runtime/operator;
   после новых таблиц повторите grants. Отдельно проверьте отказ runtime/operator DDL.
5. Запустите новую API-реплику, дождитесь readiness, проверьте consumer walkthrough
   на изолированной установке и затем переключите трафик. Старую версию выключайте
   после дренирования запросов. Очистка dedup прекращается по host CancellationToken.

## Восстановление

При сбое DDL транзакция откатывается; проверьте migration history и фактическую схему.
При сбое приложения после успешного additive migration верните прежний image,
если его совместимость с новой схемой подтверждена. Предпочитайте исправляющую
forward migration; production Down удаляет новые таблицы вместе с данными и не
является безопасным автоматическим rollback. Для восстановления из backup
остановите запись, восстановите в новую БД, проверьте инварианты и переключите
credentials после проверки. Никаких автоматических drop/recreate production БД.

SC-005 не является обязательным условием текущего этапа/релиза 1.0.0
по решению владельца от 2026-10-24. Для возможного будущего прогона нужен
отдельный стенд по quickstart; локальные Docker smoke/tests не подтверждают p95/p99.
Интеграция с реальным образом Identity проверена отдельно от JWT/JWKS fixtures
в одноразовом Development-окружении; production rollout не выполнялся.

## Проверка реального Identity

`scripts/validation/Invoke-IdentityIntegrationSmoke.ps1` запускает реальные образы
Identity/Blog/Operator, отдельные PostgreSQL 17/18.6 и Mailpit. Нужны Docker,
PowerShell 7.4+, полный SQL шести миграций Identity 1.1 и пяти миграций Blog,
полученный из соответствующих версий исходников. Blog SQL создаётся проверкой
`scripts/validation/Invoke-MigrationValidation.ps1`; Identity SQL передаётся явно.
Образы должны быть доступны локально; при необходимости получите Identity
через `docker pull danilovcpp/identity:latest`.

```powershell
pwsh -NoProfile -File scripts/validation/Invoke-IdentityIntegrationSmoke.ps1 `
  -IdentityMigrationSql 'C:/path/to/identity/migration.sql' `
  -BlogImage blog-engine-api:reviewed `
  -OperatorImage blog-engine-operator:reviewed
```

Для проверки от 2026-10-24 использованы `danilovcpp/blog:phase10`,
`blog-engine-operator:phase10` и `danilovcpp/identity:latest` с digest
`sha256:2d2019371e155137fd084c1f21e7877a0c78fa4b68a1d37f10a98ac74bc77580`.
Все 25 проверок прошли. Сценарий охватывает реальную регистрацию и подтверждение
почты, login/profile, JWKS и приём JWT, стабильный issuer/sub mapping, изоляцию
пользователей, локальные роли через Operator, создание/публикацию, wrong audience/
issuer/signature, refresh/rotation/replay и logout. После logout Identity отклоняет
профиль, а локально проверяемый JWT Blog остаётся допустим до exp + clock skew;
это существующий контракт без introspection.

HTTP-порты привязаны только к loopback; оба API работают в Development.
Blog и Identity делят network namespace для разрешённого loopback HTTP JWKS.
Это проверка совместимости реальных образов; production HTTPS/ingress этим
сценарием не проверяются. Пароли, ключи и токены генерируются для одноразовой
установки; отчёт содержит только безопасные результаты и image/schema hashes.
Сценарий удаляет свои контейнеры, сеть и временные secret-файлы.
Результат: `artifacts/validation/live-identity/result.json`.
