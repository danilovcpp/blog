# РетроТех Лаб — сборка для VPS

Готовый статический React-прототип. Бренд `retrotech`, зелёная тема `workshop`
(акцент `#276451`). На VPS нужны только веб-сервер и файлы из `site/`.
Node.js, npm, база данных и запуск Vite на сервере не требуются.

## Содержимое архива

- `site/` — сайт: `index.html`, `assets/`, `images/` и favicon.
- `nginx.conf` — пример виртуального хоста Nginx с поддержкой прямых ссылок.
- `build-info.json` — параметры и время сборки.
- `README.md` — эта инструкция.

## Если на VPS уже настроен сайт

1. Распакуйте архив на своём компьютере.
2. Загрузите **содержимое `site/`** в корень сайта через SFTP или панель хостинга.
   Файл `index.html` должен лежать непосредственно в корне сайта.
3. Для Nginx добавьте SPA fallback в существующий `server`-блок:

    ```nginx
    location / {
        try_files $uri $uri/ /index.html;
    }
    ```

Полный пример и правила кеширования есть в `nginx.conf`. Не добавляйте второй
`location /`, если он уже существует: измените существующий. Раздача нужна
с корня домена, например `https://lab.example.com/`. Размещение в подпапке
`/tech/` потребует изменения роутера и новой сборки.
Описание fallback — в [официальной документации Nginx](https://nginx.org/en/docs/http/ngx_http_core_module.html#try_files).

## Отдельный сайт на VPS с Nginx (Debian / Ubuntu)

Предполагается, что Nginx и unzip уже установлены. Загрузите ZIP в домашнюю
папку пользователя VPS, затем выполните по SSH. В первой команде подставьте
точное имя загруженного архива:

```bash
unzip retrotech-workshop-vps-YYYYMMDD-HHMMSS-fff.zip -d retrotech-release
sudo install -d -m 755 /var/www/retrotech
sudo cp -R retrotech-release/site/. /var/www/retrotech/
sudo find /var/www/retrotech -type d -exec chmod 755 {} +
sudo find /var/www/retrotech -type f -exec chmod 644 {} +
```

В `retrotech-release/nginx.conf` замените `retrotech.example` своим доменом
или IP VPS. Если каталог сайта другой, измените также `root`.
Для домена укажите DNS-запись на IP VPS.

```bash
sudo cp retrotech-release/nginx.conf /etc/nginx/sites-available/retrotech
sudo ln -s /etc/nginx/sites-available/retrotech /etc/nginx/sites-enabled/retrotech
sudo nginx -t && sudo systemctl reload nginx
```

При повторной загрузке символьную ссылку создавать повторно не нужно.
Если Nginx использует только `/etc/nginx/conf.d/`, вместо двух команд
`cp`/`ln` скопируйте конфигурацию в `/etc/nginx/conf.d/retrotech.conf`.
Готовая конфигурация слушает HTTP-порт 80. Если HTTPS уже настроен панелью
хостинга, используйте её существующий виртуальный хост и сертификат.

## Проверка после загрузки

- Откройте главную: зелёные кнопки и баннер мастерской.
- Откройте любую статью и обновите страницу: статья должна загрузиться снова.
- Откройте `/auth/login` напрямую и обновите страницу: должна появиться форма входа.
- Откройте `/prototype`: карта всех экранов и демонстрационных ролей.
- Если сайт белый, проверьте доступность `/assets/` и что `site/` не стала
  лишней вложенной папкой внутри корня сайта.

## Граница прототипа

Данные, вход, регистрация, публикации и модерация работают в режиме демонстрации
в памяти вкладки; после обновления изменения сбрасываются. Backend, Identity
и отправка писем не подключены. Используйте выдуманные данные для проверки форм.
Панель «Прототип» и тестовые роли сохранены для просмотра сценариев.

## Повторная сборка на Windows

Из `mockups/tech`, с установленными зависимостями и PowerShell 7:

```powershell
npm run package:vps
```

Архив, распакованная сборка и файл SHA256 появятся в `artifacts/` в корне
репозитория. Команда принудительно выбирает `workshop` / `retrotech`, даже если
локально настроена другая тема. Исходники и `node_modules` в архив не входят.
