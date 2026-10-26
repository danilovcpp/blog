import { useState } from "react";
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
    ChevronDown,
    FlaskConical,
    Layers,
    LayoutDashboard,
    LogOut,
    Menu,
    Settings,
    ShieldCheck,
    Tags,
    User,
    X,
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
import { Button, Modal, Toasts } from "./ui";

export function Logo({ light = false }: { light?: boolean }) {
    return (
        <Link
            to="/"
            className={`logo ${light ? "logo-light" : ""}`}
            aria-label="Слой — главная"
        >
            <span className="logo-mark">
                <Layers size={25} strokeWidth={1.7} />
            </span>
            <span>
                слой<span className="logo-dot">.</span>
            </span>
        </Link>
    );
}
export function Header() {
    const store = useMock();
    const [mobile, setMobile] = useState(false);
    const [account, setAccount] = useState(false);
    const navigate = useNavigate();
    return (
        <header className="site-header">
            <div className="header-inner">
                <Logo />
                <nav className="desktop-nav" aria-label="Главная навигация">
                    <NavLink end to="/">
                        Журнал
                    </NavLink>
                    <NavLink to="/topics">Темы</NavLink>
                    <NavLink to="/about">
                        О проекте <ArrowUpRight size={12} />
                    </NavLink>
                </nav>
                <div className="header-actions">
                    {store.authed ? (
                        <>
                            <Link
                                className="header-workspace"
                                to={
                                    store.hasRole("Author")
                                        ? "/workspace"
                                        : store.hasRole("Moderator")
                                          ? "/moderation"
                                          : "/account/profile"
                                }
                            >
                                {store.hasRole("Author")
                                    ? "Моя редакция"
                                    : store.hasRole("Moderator")
                                      ? "Модерация"
                                      : "Мой аккаунт"}
                            </Link>
                            <div className="account-menu-anchor">
                                <button
                                    className="profile-trigger"
                                    aria-expanded={account}
                                    aria-label="Меню аккаунта"
                                    onClick={() => setAccount(!account)}
                                >
                                    <span className="avatar">
                                        {store.displayName[0] || "А"}
                                    </span>
                                    <ChevronDown size={14} />
                                </button>
                                {account && (
                                    <div className="account-popover">
                                        <span className="menu-caption">
                                            {store.displayName || "Ваш аккаунт"}
                                        </span>
                                        <Link
                                            to="/account/profile"
                                            onClick={() => setAccount(false)}
                                        >
                                            <User size={17} />
                                            Мой профиль
                                        </Link>
                                        <Link
                                            to="/account/security"
                                            onClick={() => setAccount(false)}
                                        >
                                            <Settings size={17} />
                                            Безопасность
                                        </Link>
                                        <button
                                            onClick={() => {
                                                store.setPersona("guest");
                                                setAccount(false);
                                                store.toast(
                                                    "Вы вышли из аккаунта",
                                                );
                                                navigate("/");
                                            }}
                                        >
                                            <LogOut size={17} />
                                            Выйти
                                        </button>
                                    </div>
                                )}
                            </div>
                        </>
                    ) : (
                        <Link className="login-link" to="/auth/login">
                            Войти <ArrowUpRight size={16} />
                        </Link>
                    )}
                    <button
                        aria-label={mobile ? "Закрыть меню" : "Открыть меню"}
                        className="icon-button mobile-menu-button"
                        onClick={() => setMobile(!mobile)}
                    >
                        {mobile ? <X size={22} /> : <Menu size={22} />}
                    </button>
                </div>
            </div>
            {mobile && (
                <nav className="mobile-nav" aria-label="Мобильная навигация">
                    {[
                        ["/", "Журнал"],
                        ["/topics", "Темы"],
                        ["/about", "О проекте"],
                        ["/workspace", "Моя редакция"],
                        ["/account/profile", "Аккаунт"],
                    ].map(([to, label]) => (
                        <Link key={to} to={to} onClick={() => setMobile(false)}>
                            {label}
                            <ArrowRight size={16} />
                        </Link>
                    ))}
                </nav>
            )}
        </header>
    );
}
export function Footer() {
    const { authed } = useMock();
    return (
        <footer className="site-footer">
            <div className="container footer-main">
                <div>
                    <Logo />
                    <p>
                        Пространство для любопытства.
                        <br />И для тех, кто смотрит чуть глубже.
                    </p>
                </div>
                <div className="footer-links">
                    <Link to="/">Журнал</Link>
                    <Link to="/topics">Все темы</Link>
                    <Link to="/about">О проекте</Link>
                    <Link to={authed ? "/account/profile" : "/auth/register"}>
                        {authed ? "Мой аккаунт" : "Присоединиться"}{" "}
                        <ArrowUpRight size={14} />
                    </Link>
                </div>
            </div>
            <div className="container footer-bottom">
                <span>© 2026 Слой. Создано с вниманием.</span>
                <span>
                    Делимся мыслями, а не спешкой <ArrowDownRight size={14} />
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
                        Вернуться в журнал
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
