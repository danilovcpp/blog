import { useEffect, useRef, useState } from "react";
import {
    Link,
    NavLink,
    Outlet,
    useLocation,
    useNavigate,
} from "react-router-dom";
import {
    ArrowDownRight,
    ArrowRight,
    ArrowUpRight,
    BookOpen,
    Bookmark,
    ChevronDown,
    Cpu,
    FlaskConical,
    LayoutDashboard,
    LogOut,
    Menu,
    Search,
    Settings,
    ShieldCheck,
    Tags,
    User,
    X,
    PenLine,
    Bell,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import {
    useMock,
    personas,
    scenarios,
    type Persona,
    type Scenario,
} from "../mock/store";
import { theme, themeName } from "../themes";
import { Button, Modal } from "./ui";
import { brand } from "../brands";
const Layers = Cpu;
export function Logo({ light = false }: { light?: boolean }) {
    const Mark = brand.logo === "cpu" ? Cpu : BookOpen;
    return (
        <Link
            to="/"
            className={`logo ${light ? "logo-light" : ""}`}
            aria-label={`${brand.name} — главная`}
        >
            <span className="logo-mark">
                <Mark size={33} strokeWidth={1.6} />
            </span>
            <span className="logo-type">
                {brand.wordmark[0]} <em>{brand.wordmark[1]}</em>
                <small>{brand.tagline}</small>
            </span>
        </Link>
    );
}
export function Header() {
    const store = useMock();
    const navigate = useNavigate();
    const location = useLocation();
    const [mobile, setMobile] = useState(false);
    const [account, setAccount] = useState(false);
    const [notifications, setNotifications] = useState(false);
    const [query, setQuery] = useState("");
    const searchRef = useRef<HTMLInputElement>(null);
    const menuRef = useRef<HTMLDivElement>(null);
    useEffect(() => {
        setMobile(false);
        setAccount(false);
        setNotifications(false);
    }, [location.pathname]);
    useEffect(() => {
        const handler = (e: KeyboardEvent) => {
            if ((e.ctrlKey || e.metaKey) && e.key === "k") {
                e.preventDefault();
                if (
                    searchRef.current &&
                    getComputedStyle(searchRef.current).display !== "none"
                )
                    searchRef.current.focus();
                else navigate("/search");
            }
            if (e.key === "Escape") {
                setMobile(false);
                setAccount(false);
                setNotifications(false);
            }
        };
        const close = (e: MouseEvent) => {
            if (!menuRef.current?.contains(e.target as Node)) {
                setAccount(false);
                setNotifications(false);
            }
        };
        window.addEventListener("keydown", handler);
        window.addEventListener("click", close);
        return () => {
            window.removeEventListener("keydown", handler);
            window.removeEventListener("click", close);
        };
    }, [navigate]);
    const replies = store.comments
        .filter(
            (c) =>
                c.state === "Visible" &&
                c.parentId &&
                c.author.id !== "10000000-0000-4000-8000-000000000001" &&
                store.comments.some(
                    (p) =>
                        p.id === c.parentId &&
                        p.author.id === "10000000-0000-4000-8000-000000000001",
                ),
        )
        .slice(0, 3);
    return (
        <header className="site-header">
            <div className="header-inner">
                <Logo />
                <nav className="desktop-nav" aria-label="Главная навигация">
                    {brand.headerNav.map(([href, label]) => (
                        <NavLink key={href} end={href === "/"} to={href}>
                            {label}
                        </NavLink>
                    ))}
                </nav>
                <form
                    className="header-search"
                    role="search"
                    onSubmit={(e) => {
                        e.preventDefault();
                        navigate(`/search?q=${encodeURIComponent(query)}`);
                    }}
                >
                    <Search size={19} />
                    <input
                        ref={searchRef}
                        aria-label="Поиск статей"
                        value={query}
                        onChange={(e) => setQuery(e.target.value)}
                        placeholder={brand.searchPlaceholder}
                    />
                    <kbd>Ctrl + K</kbd>
                </form>
                <div className="header-actions" ref={menuRef}>
                    <div className="notification-anchor">
                        <button
                            className="notification-trigger"
                            aria-label="Ответы на комментарии"
                            aria-expanded={notifications}
                            onClick={() => {
                                setNotifications(!notifications);
                                setAccount(false);
                            }}
                        >
                            <Bell size={22} strokeWidth={1.6} />
                            {store.authed && replies.length > 0 && <span />}
                        </button>
                        {notifications && (
                            <div className="account-popover notification-popover">
                                <span className="menu-caption">
                                    Ответы в обсуждениях
                                </span>
                                {store.authed ? (
                                    replies.length ? (
                                        replies.map((reply) => (
                                            <Link
                                                key={reply.id}
                                                to={`/posts/${store.posts.find((p) => p.id === reply.publicationId)?.slug}#comments`}
                                            >
                                                <MessageCircleIcon />
                                                <span>{reply.content}</span>
                                            </Link>
                                        ))
                                    ) : (
                                        <p>Пока нет новых ответов.</p>
                                    )
                                ) : (
                                    <>
                                        <p>
                                            Войдите, чтобы следить за ответами
                                            на ваши комментарии.
                                        </p>
                                        <Link to="/auth/login">
                                            <User size={16} />
                                            Войти
                                        </Link>
                                    </>
                                )}
                                <small>Тестовые данные прототипа</small>
                            </div>
                        )}
                    </div>
                    {store.authed ? (
                        <div className="account-menu-anchor">
                            <button
                                className="profile-trigger"
                                aria-label="Меню аккаунта"
                                aria-expanded={account}
                                onClick={() => {
                                    setAccount(!account);
                                    setNotifications(false);
                                }}
                            >
                                <span className="avatar">
                                    <User size={21} strokeWidth={1.5} />
                                </span>
                            </button>
                            {account && (
                                <div className="account-popover">
                                    <span className="menu-caption">
                                        {store.displayName}
                                    </span>
                                    <Link to="/account/profile">
                                        <User size={16} />
                                        Мой профиль
                                    </Link>
                                    <Link to="/bookmarks">
                                        <Bookmark size={16} />
                                        Закладки
                                    </Link>
                                    {store.hasRole("Author") && (
                                        <Link to="/workspace">
                                            <BookOpen size={16} />
                                            Мои материалы
                                        </Link>
                                    )}
                                    {store.hasRole("Moderator") && (
                                        <Link to="/moderation">
                                            <ShieldCheck size={16} />
                                            Модерация
                                        </Link>
                                    )}
                                    {store.hasRole("Admin") && (
                                        <Link to="/admin/taxonomy">
                                            <Tags size={16} />
                                            Категории и теги
                                        </Link>
                                    )}
                                    <Link to="/account/security">
                                        <Settings size={16} />
                                        Безопасность
                                    </Link>
                                    <Link to="/account/sessions">
                                        <LayoutDashboard size={16} />
                                        Мои сеансы
                                    </Link>
                                    <button
                                        onClick={() => {
                                            store.setSessions((items) =>
                                                items.filter(
                                                    (s) => !s.isCurrent,
                                                ),
                                            );
                                            store.setPersona("guest");
                                            navigate("/");
                                            store.toast("Вы вышли из аккаунта");
                                        }}
                                    >
                                        <LogOut size={16} />
                                        Выйти
                                    </button>
                                </div>
                            )}
                        </div>
                    ) : (
                        <Link
                            className="guest-profile"
                            aria-label="Войти в аккаунт"
                            to={`/auth/login?returnTo=${encodeURIComponent(location.pathname + location.search)}`}
                        >
                            <User size={22} strokeWidth={1.5} />
                        </Link>
                    )}
                    <button
                        className="icon-button mobile-menu-button"
                        aria-label={mobile ? "Закрыть меню" : "Открыть меню"}
                        aria-expanded={mobile}
                        onClick={() => setMobile(!mobile)}
                    >
                        {mobile ? <X size={21} /> : <Menu size={21} />}
                    </button>
                </div>
            </div>
            {mobile && (
                <nav className="mobile-nav" aria-label="Мобильная навигация">
                    {[
                        ...brand.headerNav,
                        ["/search", "Поиск"],
                        ["/bookmarks", "Закладки"],
                        ["/workspace", "Мои материалы"],
                        ["/account/profile", "Мой аккаунт"],
                    ].map(([to, label]) => (
                        <Link key={to + label} to={to}>
                            {label}
                            <ArrowRight size={16} />
                        </Link>
                    ))}
                </nav>
            )}
        </header>
    );
}
function MessageCircleIcon() {
    return <BookOpen size={16} />;
}
export function Footer() {
    return (
        <footer className="site-footer">
            <div className="container footer-main">
                <Logo />
                <div className="footer-links">
                    <Link to="/about">О проекте</Link>
                    <Link to="/about">Правила сообщества</Link>
                    <Link to="/topics">Направления</Link>
                    <Link to="/prototype">
                        Карта экранов <ArrowUpRight size={13} />
                    </Link>
                </div>
                <span className="footer-motto">
                    {brand.footer.motto[0]}
                    <br />
                    <span>{brand.footer.motto[1]}</span>
                </span>
            </div>
            <div className="container footer-bottom">
                <span>© 2026 {brand.name}</span>
                <span>
                    <Cpu size={13} />
                    {brand.footer.caption}
                </span>
            </div>
        </footer>
    );
}
export function DemoTools() {
    const [open, setOpen] = useState(false);
    const store = useMock();
    const path = useLocation().pathname;
    return (
        <>
            <button
                className="demo-trigger"
                onClick={() => setOpen(true)}
                aria-label="Открыть настройки прототипа"
            >
                <FlaskConical size={15} />
                <span>Прототип</span>
                <span className="demo-dot" />
                <span>{personas[store.persona].name}</span>
            </button>
            <Modal
                open={open}
                onClose={() => setOpen(false)}
                title="Исследуйте прототип"
            >
                <p className="muted">
                    Все данные тестовые. Действия изменяют только состояние этой
                    вкладки.
                </p>
                <div className="field">
                    <label htmlFor="demo-persona">Посмотреть от лица</label>
                    <select
                        id="demo-persona"
                        value={store.persona}
                        onChange={(e) =>
                            store.setPersona(e.target.value as Persona)
                        }
                    >
                        {Object.entries(personas).map(([key, p]) => (
                            <option key={key} value={key}>
                                {p.name}
                            </option>
                        ))}
                    </select>
                    <span className="field-hint">
                        Это инструмент демонстрации, а не назначение настоящих
                        ролей.
                    </span>
                </div>
                <div className="field">
                    <label htmlFor="demo-state">Состояние интерфейса</label>
                    <select
                        id="demo-state"
                        value={store.scenario}
                        onChange={(e) =>
                            store.setScenario(e.target.value as Scenario)
                        }
                    >
                        {Object.entries(scenarios).map(([key, label]) => (
                            <option key={key} value={key}>
                                {label}
                            </option>
                        ))}
                    </select>
                </div>
                <div className="theme-note">
                    <span className="theme-swatch" />
                    Тема сборки: {theme.name}
                    <small>
                        VITE_THEME={themeName} · без runtime-переключения
                    </small>
                </div>
                <Link
                    className="button button-secondary full"
                    to="/prototype"
                    onClick={() => setOpen(false)}
                >
                    Карта страниц и сценариев <ArrowUpRight size={16} />
                </Link>
                <div className="modal-footer">
                    <Button variant="ghost" onClick={store.reset}>
                        Сбросить данные
                    </Button>
                    <Button onClick={() => setOpen(false)}>Продолжить</Button>
                </div>
                <span className="tiny muted">Текущая страница: {path}</span>
            </Modal>
        </>
    );
}
export function PublicLayout() {
    return (
        <>
            <Header />
            <Outlet />
            <Footer />
        </>
    );
}
export function AppLayout({ account = false }: { account?: boolean }) {
    const { hasRole } = useMock();
    const links: ReadonlyArray<readonly [string, string, LucideIcon]> = account
        ? [
              ["/account/profile", "Мой профиль", User],
              ["/account/security", "Безопасность", ShieldCheck],
              ["/account/sessions", "Активные сеансы", LayoutDashboard],
              ["/account/deactivate", "Деактивация", LogOut],
          ]
        : [
              ...(hasRole("Author")
                  ? [["/workspace", "Материалы", BookOpen] as const]
                  : []),
              ...(hasRole("Moderator")
                  ? [["/moderation", "Модерация", ShieldCheck] as const]
                  : []),
              ...(hasRole("Admin")
                  ? [["/admin/taxonomy", "Категории и теги", Tags] as const]
                  : []),
          ];
    return (
        <>
            <Header />
            <div className="app-layout container">
                <aside className="app-sidebar">
                    <span className="eyebrow">
                        {account ? "ЛИЧНОЕ ПРОСТРАНСТВО" : "ВАША РЕДАКЦИЯ"}
                    </span>
                    <h2>{account ? "Аккаунт" : "За кулисами"}</h2>
                    <nav
                        aria-label={account ? "Настройки аккаунта" : "Редакция"}
                    >
                        {links.map(([to, label, Icon]) => (
                            <NavLink key={to} to={to} end>
                                <Icon size={18} />
                                {label}
                            </NavLink>
                        ))}
                    </nav>
                    <Link className="sidebar-back" to="/">
                        <ArrowUpRight size={16} />
                        Вернуться в ленту
                    </Link>
                    <div className="sidebar-note">
                        <Layers size={24} />
                        <p>
                            У каждой хорошей истории
                            <br />
                            есть первый черновик.
                        </p>
                    </div>
                </aside>
                <main className="app-main">
                    <Outlet />
                </main>
            </div>
            <Footer />
        </>
    );
}
