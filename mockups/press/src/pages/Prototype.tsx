import { Link } from "react-router-dom";
import { useState } from "react";
import {
    ArrowUpRight,
    Check,
    FlaskConical,
    Layers,
    Palette,
} from "lucide-react";
import {
    useMock,
    personas,
    scenarios,
    type Persona,
    type Scenario,
} from "../mock/store";
import { pid } from "../mock/data";
import { theme, themeName } from "../themes";
import { Badge, Button } from "../components/ui";

const sections: {
    title: string;
    description: string;
    links: [string, string, Persona, Scenario?][];
}[] = [
    {
        title: "01 / Журнал",
        description: "Открытие, чтение и разговор.",
        links: [
            ["Главная и лента", "/", "guest"],
            ["Популярное", "/?sort=popular", "reader"],
            ["Все темы", "/topics", "guest"],
            ["Коллекция категории", "/category/architecture", "guest"],
            ["Подборка по тегу", "/tag/attention", "guest"],
            ["Материал и обсуждение", "/posts/a-place-to-slow-down", "reader"],
            ["О проекте", "/about", "guest"],
            [
                "Собственный скрытый комментарий",
                "/comments/50000000-0000-4000-8000-000000000003/manage",
                "reader",
            ],
        ],
    },
    {
        title: "02 / Редакция",
        description: "От первого черновика до публикации.",
        links: [
            ["Материалы автора", "/workspace", "author"],
            ["Новый черновик", "/workspace/new", "author"],
            ["Редактор черновика", `/workspace/${pid(10)}/edit`, "author"],
            ["Редактирование Published", `/workspace/${pid(1)}/edit`, "author"],
            ["Снятый с публикации", `/workspace/${pid(11)}/edit`, "author"],
            [
                "Конфликт сохранения",
                `/workspace/${pid(10)}/edit`,
                "author",
                "conflict",
            ],
            ["Нет доступа к редакции", "/workspace", "reader"],
        ],
    },
    {
        title: "03 / Порядок в журнале",
        description: "Модерация без очереди одобрения.",
        links: [
            ["Модерация комментариев", "/moderation", "moderator"],
            ["Модерация с правами Admin", "/moderation", "admin"],
            ["Категории и теги", "/admin/taxonomy", "admin"],
            ["Недостаточно прав", "/admin/taxonomy", "author"],
        ],
    },
    {
        title: "04 / Знакомство и доступ",
        description: "Все пользовательские сценарии Identity.",
        links: [
            ["Вход", "/auth/login", "guest"],
            ["Неверные данные", "/auth/login", "guest", "invalid"],
            ["Ограничение попыток", "/auth/login", "guest", "rate-limit"],
            ["Регистрация", "/auth/register", "guest"],
            ["Проверьте почту", "/auth/check-email", "guest"],
            ["Повторное письмо", "/auth/resend-verification", "guest"],
            ["Подтверждение email", "/auth/verify-email?demo=valid", "guest"],
            ["Недействительная ссылка", "/auth/verify-email", "guest"],
            ["Забыли пароль", "/auth/forgot-password", "guest"],
            ["Новый пароль", "/auth/reset-password?demo=valid", "guest"],
            ["Просроченный reset", "/auth/reset-password", "guest"],
            [
                "Принять приглашение",
                "/auth/accept-invitation?demo=valid",
                "guest",
            ],
            [
                "Недействительное приглашение",
                "/auth/accept-invitation",
                "guest",
            ],
            ["Сеанс завершён", "/auth/session-expired", "guest"],
        ],
    },
    {
        title: "05 / Личное пространство",
        description: "Профиль, безопасность и контроль сеансов.",
        links: [
            ["Профиль", "/account/profile", "reader"],
            ["Изменение пароля", "/account/security", "reader"],
            ["Активные сеансы", "/account/sessions", "reader"],
            ["Деактивация аккаунта", "/account/deactivate", "reader"],
            ["Аккаунт деактивирован", "/auth/deactivated", "guest"],
        ],
    },
    {
        title: "06 / Пограничные состояния",
        description: "Спокойная обратная связь, когда что-то не так.",
        links: [
            ["Загрузка ленты", "/", "guest", "loading"],
            ["Нет материалов", "/", "guest", "empty"],
            ["Нет подключения", "/", "guest", "offline"],
            ["Сервис недоступен", "/", "reader", "unavailable"],
            ["Сбой следующей страницы", "/", "reader", "page-error"],
            ["Материал недоступен", "/posts/not-found", "guest"],
            ["Сессия истекла", "/auth/session-expired", "guest"],
        ],
    },
];
export function Prototype() {
    const store = useMock();
    return (
        <main className="container prototype-page">
            <div className="page-heading">
                <span className="eyebrow">
                    <FlaskConical size={15} />
                    ИНТЕРАКТИВНЫЙ ПРОТОТИП
                </span>
                <h1>
                    Весь Слой.
                    <br />
                    <em>Во всех состояниях.</em>
                </h1>
                <p>
                    Карта интерфейса для исследования, обсуждения и передачи в
                    разработку.
                </p>
            </div>
            <Link
                className="button button-secondary"
                to="/prototype/responsive"
            >
                Проверить мобильную компоновку <ArrowUpRight size={16} />
            </Link>
            <div className="prototype-intro">
                <div>
                    <Layers size={23} />
                    <strong>Локальные данные</strong>
                    <p>
                        Нет API-запросов, настоящих аккаунтов и отправки писем.
                        Изменения живут в этой вкладке и сбрасываются при
                        перезагрузке.
                    </p>
                </div>
                <div>
                    <Palette size={23} />
                    <strong>{theme.name}</strong>
                    <p>
                        Тема задаётся до сборки:{" "}
                        <code>VITE_THEME={themeName}</code>. Цвета, обложки и
                        атмосфера меняются одним brand package.
                    </p>
                </div>
            </div>
            <div className="prototype-grid">
                {sections.map((section) => (
                    <section key={section.title} className="prototype-section">
                        <h2>{section.title}</h2>
                        <p>{section.description}</p>
                        <div>
                            {section.links.map(
                                ([title, to, persona, scenario]) => (
                                    <Link
                                        key={title}
                                        to={to}
                                        onClick={() => {
                                            store.setPersona(persona);
                                            store.setScenario(
                                                scenario || "normal",
                                            );
                                        }}
                                    >
                                        <span>{title}</span>
                                        <span>
                                            <Badge>
                                                {personas[persona].name}
                                            </Badge>
                                            <ArrowUpRight size={16} />
                                        </span>
                                    </Link>
                                ),
                            )}
                        </div>
                    </section>
                ))}
            </div>
            <div className="prototype-end">
                <p>
                    Скрытие, публикация, удаление, конфликты и завершение
                    сеансов доступны из соответствующих экранов и диалогов.
                </p>
                <Button variant="secondary" onClick={store.reset}>
                    Вернуть исходные данные
                </Button>
            </div>
        </main>
    );
}

/** A real iframe viewport: media queries run at 390/768 px, without browser emulation. */
export function ViewportPreview() {
    const [width, setWidth] = useState(390);
    const [page, setPage] = useState("/");
    return (
        <main className="viewport-preview">
            <header>
                <Link className="text-link" to="/prototype">
                    ← Карта прототипа
                </Link>
                <label>
                    Экран{" "}
                    <select
                        value={width}
                        onChange={(e) => setWidth(Number(e.target.value))}
                    >
                        <option value={390}>Телефон · 390 px</option>
                        <option value={768}>Планшет · 768 px</option>
                    </select>
                </label>
                <label>
                    Страница{" "}
                    <select
                        value={page}
                        onChange={(e) => setPage(e.target.value)}
                    >
                        <option value="/">Журнал</option>
                        <option value="/topics">Темы</option>
                        <option value="/posts/a-place-to-slow-down">
                            Материал
                        </option>
                        <option value="/auth/login">Вход</option>
                        <option value="/auth/register">Регистрация</option>
                        <option value="/auth/reset-password?demo=valid">
                            Новый пароль
                        </option>
                        <option value="/prototype">Все роли и сценарии</option>
                    </select>
                </label>
            </header>
            <div className="viewport-stage">
                <iframe
                    key={page}
                    src={page}
                    title="Адаптивный интерфейс Слоя"
                    style={{ width, height: 844 }}
                />
            </div>
        </main>
    );
}
