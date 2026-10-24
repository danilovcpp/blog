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

При SC-005 нужен отдельный стенд по quickstart; локальные Docker smoke/tests
не подтверждают p95/p99. Реальный Identity и production rollout проверяются
отдельно от одноразовых JWT/JWKS fixtures и в текущем отчёте не заявляются.
