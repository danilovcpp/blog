import { brand } from "../brands";
import { useEffect, useRef, useState } from "react";
import {
    Link,
    useLocation,
    useNavigate,
    useSearchParams,
} from "react-router-dom";
import {
    ArrowLeft,
    ArrowRight,
    ArrowUpRight,
    Check,
    CheckCircle2,
    ChevronRight,
    Eye,
    EyeOff,
    KeyRound,
    LockKeyhole,
    Mail,
    Monitor,
    ShieldCheck,
    Smartphone,
    Trash2,
    User,
    X,
} from "lucide-react";
import { Logo } from "../components/Layout";
import {
    Alert,
    Badge,
    Button,
    EmptyState,
    Field,
    Modal,
    ScenarioState,
} from "../components/ui";
import { useMock, delay } from "../mock/store";
import { formatDate } from "../mock/data";
import { theme } from "../themes";
import { passwordProblem } from "../mock/rules";

type AuthKind =
    | "login"
    | "register"
    | "check-email"
    | "verify-email"
    | "resend-verification"
    | "forgot-password"
    | "reset-password"
    | "accept-invitation"
    | "session-expired"
    | "deactivated";
const configs: Record<
    AuthKind,
    { eyebrow: string; title: string; description: string }
> = {
    login: {
        eyebrow: "РАДЫ ВАС ВИДЕТЬ",
        title: "С возвращением.",
        description:
            "Войдите, чтобы продолжить разговор и поддержать близкие вам идеи.",
    },
    register: {
        eyebrow: "МЕСТО ДЛЯ ВАШЕГО ВЗГЛЯДА",
        title: "Давайте знакомиться.",
        description:
            "Хорошие истории объединяют. Станьте частью нашего небольшого сообщества.",
    },
    "check-email": {
        eyebrow: "ЕЩЁ ОДИН МАЛЕНЬКИЙ ШАГ",
        title: "Загляните в почту.",
        description:
            "Если для этого адреса можно выполнить запрос, мы отправим письмо с дальнейшими шагами.",
    },
    "verify-email": {
        eyebrow: "ПОДТВЕРЖДЕНИЕ ПОЧТЫ",
        title: "Это ваш адрес?",
        description:
            "Подтвердите почту, чтобы войти в аккаунт и присоединиться к разговору.",
    },
    "resend-verification": {
        eyebrow: "ПИСЬМО НЕ НАШЛОСЬ?",
        title: "Отправим ещё раз.",
        description: "Укажите email, который вы использовали при регистрации.",
    },
    "forgot-password": {
        eyebrow: "ТАКОЕ СЛУЧАЕТСЯ",
        title: "Забыли пароль?",
        description:
            "Укажите email аккаунта. Мы подскажем, как задать новый пароль.",
    },
    "reset-password": {
        eyebrow: "НОВЫЙ ПАРОЛЬ",
        title: "Задайте новый пароль.",
        description:
            "Придумайте длинный пароль, который легко запомнить вам и сложно угадать другим.",
    },
    "accept-invitation": {
        eyebrow: "ВАС ЖДАЛИ",
        title: `Добро пожаловать в ${brand.name}.`,
        description:
            "Осталось задать пароль. Приглашение активирует ваш аккаунт.",
    },
    "session-expired": {
        eyebrow: "ПОРА ПОЗНАКОМИТЬСЯ СНОВА",
        title: "Сеанс завершён.",
        description:
            "Возможно, вы вышли на другом устройстве или изменили пароль. Войдите снова, чтобы продолжить.",
    },
    deactivated: {
        eyebrow: "ДО ВСТРЕЧИ",
        title: "Аккаунт деактивирован.",
        description:
            "Все сеансы завершены. Восстановление через публичный интерфейс не предусмотрено. Это не удаление персональных данных.",
    },
};

export function PasswordInput({
    id,
    value,
    onChange,
    autoComplete = "new-password",
    error,
    placeholder = "Не меньше 15 символов",
}: {
    id: string;
    value: string;
    onChange: (value: string) => void;
    autoComplete?: string;
    error?: boolean;
    placeholder?: string;
}) {
    const [show, setShow] = useState(false);
    return (
        <div className="password-input">
            <input
                id={id}
                type={show ? "text" : "password"}
                value={value}
                onChange={(e) => onChange(e.target.value)}
                autoComplete={autoComplete}
                aria-invalid={error}
                placeholder={placeholder}
            />
            <button
                type="button"
                aria-label={show ? "Скрыть пароль" : "Показать пароль"}
                aria-pressed={show}
                onClick={() => setShow(!show)}
            >
                {show ? <EyeOff size={18} /> : <Eye size={18} />}
            </button>
        </div>
    );
}
export const passwordError = passwordProblem;

export function AuthPage({ kind }: { kind: AuthKind }) {
    const store = useMock();
    const navigate = useNavigate();
    const location = useLocation();
    const [params] = useSearchParams();
    const [email, setEmail] = useState("");
    const [displayName, setDisplayName] = useState("");
    const [password, setPassword] = useState("");
    const [confirmation, setConfirmation] = useState("");
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const [errors, setErrors] = useState<Record<string, string>>({});
    const [success, setSuccess] = useState(false);
    const [cooldown, setCooldown] = useState(0);
    const tokenRef = useRef<string | null>(null);
    const [hasToken, setHasToken] = useState(false);
    useEffect(() => {
        const fragment = new URLSearchParams(window.location.hash.slice(1));
        const token = fragment.get("token");
        if (token) {
            tokenRef.current = token;
            history.replaceState(
                history.state,
                "",
                window.location.pathname + window.location.search,
            );
        }
        setHasToken(!!tokenRef.current);
        setSuccess(false);
        setError("");
        setErrors({});
        setPassword("");
        setConfirmation("");
    }, [kind]);
    useEffect(() => {
        if (cooldown <= 0) return;
        const timer = setTimeout(() => setCooldown((t) => t - 1), 1000);
        return () => clearTimeout(timer);
    }, [cooldown]);
    const cfg = configs[kind];
    const linkFlow = [
        "verify-email",
        "reset-password",
        "accept-invitation",
    ].includes(kind);
    const validLink =
        (hasToken || params.get("demo") === "valid") &&
        store.scenario !== "invalid";
    const needsEmail = [
        "login",
        "register",
        "resend-verification",
        "forgot-password",
    ].includes(kind);
    const newPassword = [
        "register",
        "reset-password",
        "accept-invitation",
    ].includes(kind);
    const hasPassword = kind === "login" || newPassword;
    const submit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError("");
        const next: Record<string, string> = {};
        if (
            needsEmail &&
            (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 320)
        )
            next.email = "Введите корректный адрес электронной почты.";
        if (hasPassword && !password) next.password = "Введите пароль.";
        if (newPassword) {
            const p = passwordError(password);
            if (p) next.password = p;
            if (password !== confirmation)
                next.confirmation = "Пароли должны совпадать.";
        }
        if (kind === "register" && [...displayName.trim()].length > 120)
            next.displayName = "Не больше 120 символов.";
        setErrors(next);
        if (Object.keys(next).length) {
            setTimeout(
                () =>
                    document
                        .querySelector<HTMLElement>('[aria-invalid="true"]')
                        ?.focus(),
                0,
            );
            return;
        }
        setBusy(true);
        try {
            await delay(650);
            store.assertAvailable();
            if (store.scenario === "rate-limit") {
                setCooldown(30);
                throw new Error(
                    "Слишком много попыток. Подождите 30 секунд и попробуйте снова.",
                );
            }
            if (store.scenario === "invalid")
                throw new Error(
                    kind === "login"
                        ? "Не удалось войти. Проверьте email и пароль. Если почта не подтверждена, запросите новое письмо."
                        : "Не удалось выполнить запрос. Проверьте введённые данные.",
                );
            if (kind === "login") {
                if (!store.accountActive)
                    throw new Error(
                        "Не удалось войти. Проверьте email и пароль.",
                    );
                store.setPersona("reader");
                store.setDisplayName(brand.authors[0]);
                store.setSessions((items) => [
                    ...items.filter((s) => !s.isCurrent),
                    {
                        id: crypto.randomUUID(),
                        clientId: brand.id,
                        userAgent: "Текущий браузер",
                        createdAt: new Date().toISOString(),
                        lastUsedAt: new Date().toISOString(),
                        absoluteExpiresAt: new Date(
                            Date.now() + 30 * 86400000,
                        ).toISOString(),
                        isCurrent: true,
                    },
                ]);
                const ret = params.get("returnTo");
                navigate(
                    ret?.startsWith("/") && !ret.startsWith("//") ? ret : "/",
                );
                store.toast(`Вы вошли в ${brand.name}`);
            } else if (
                ["register", "forgot-password", "resend-verification"].includes(
                    kind,
                )
            ) {
                setSuccess(true);
                setCooldown(30);
            } else {
                tokenRef.current = null;
                setSuccess(true);
                if (kind === "reset-password") {
                    store.setSessions([]);
                    store.setPersona("guest");
                }
            }
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    const resend = async () => {
        setBusy(true);
        try {
            await delay();
            store.assertAvailable();
            setCooldown(30);
            store.toast(
                "Если запрос подходит для адреса, письмо будет отправлено.",
            );
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    const staticState =
        kind === "check-email" ||
        kind === "session-expired" ||
        kind === "deactivated";
    return (
        <main className="auth-layout">
            <aside className="auth-visual">
                <img src={theme.auth} alt={theme.authAlt} />
                <div className="auth-visual-shade" />
                <Logo light />
                <div className="auth-visual-copy">
                    <span className="auth-star">✳</span>
                    <h2>
                        {brand.auth.quote[0]}
                        <br />
                        {brand.auth.quote[1]}
                        <br />
                        <em>
                            {brand.auth.quote[2]}
                            <br />
                            {brand.auth.quote[3]}
                        </em>
                    </h2>
                    <p>{brand.auth.subtitle}</p>
                </div>
                <span className="auth-visual-bottom">{brand.tagline}</span>
            </aside>
            <section className="auth-right">
                <Link className="auth-back" to="/">
                    <ArrowLeft size={16} />В ленту
                </Link>
                <div className="auth-form-wrap">
                    <span className="eyebrow">{cfg.eyebrow}</span>
                    {linkFlow && !validLink && !success ? (
                        <>
                            <span className="auth-state-icon">
                                <KeyRound size={30} />
                            </span>
                            <h1>Ссылка недействительна.</h1>
                            <p>
                                Ссылка недействительна или больше не может быть
                                использована. Запросите новую, чтобы продолжить.
                            </p>
                            <Link
                                className="button button-primary full"
                                to={
                                    kind === "verify-email"
                                        ? "/auth/resend-verification"
                                        : kind === "accept-invitation"
                                          ? "/auth/login"
                                          : "/auth/forgot-password"
                                }
                            >
                                {kind === "verify-email"
                                    ? "Отправить новое письмо"
                                    : kind === "accept-invitation"
                                      ? "Вернуться ко входу"
                                      : "Получить новую ссылку"}
                                <ArrowRight size={16} />
                            </Link>
                            {kind === "accept-invitation" && (
                                <p className="tiny muted">
                                    За новым приглашением обратитесь к тому, кто
                                    вас пригласил.
                                </p>
                            )}
                        </>
                    ) : success ? (
                        <>
                            <span className="auth-state-icon success">
                                <CheckCircle2 size={34} />
                            </span>
                            <h1>
                                {[
                                    "register",
                                    "resend-verification",
                                    "forgot-password",
                                ].includes(kind)
                                    ? "Проверьте почту."
                                    : kind === "verify-email"
                                      ? "Почта подтверждена."
                                      : kind === "accept-invitation"
                                        ? "Теперь вы с нами."
                                        : "Пароль обновлён."}
                            </h1>
                            <p>
                                {[
                                    "register",
                                    "resend-verification",
                                    "forgot-password",
                                ].includes(kind)
                                    ? "Если для этого адреса подходит запрос, мы отправим письмо с дальнейшими шагами. Проверьте также папку «Спам»."
                                    : kind === "reset-password"
                                      ? "Все предыдущие сеансы завершены. Войдите с новым паролем."
                                      : `Можно войти в аккаунт и вернуться в ${brand.name}.`}
                            </p>
                            {[
                                "register",
                                "resend-verification",
                                "forgot-password",
                            ].includes(kind) && (
                                <Button
                                    variant="secondary"
                                    className="full"
                                    busy={busy}
                                    disabled={cooldown > 0}
                                    onClick={resend}
                                >
                                    <Mail size={16} />
                                    {cooldown
                                        ? `Отправить повторно через ${cooldown} с`
                                        : "Отправить ещё раз"}
                                </Button>
                            )}
                            <Link
                                className="button button-primary full"
                                to="/auth/login"
                            >
                                Перейти ко входу <ArrowRight size={16} />
                            </Link>
                            {error && <Alert tone="error">{error}</Alert>}
                        </>
                    ) : (
                        <>
                            <h1>{cfg.title}</h1>
                            <p className="auth-description">
                                {cfg.description}
                            </p>
                            {staticState ? (
                                <>
                                    <span className="auth-state-icon">
                                        {kind === "check-email" ? (
                                            <Mail size={32} />
                                        ) : kind === "deactivated" ? (
                                            <User size={32} />
                                        ) : (
                                            <LockKeyhole size={32} />
                                        )}
                                    </span>
                                    {kind === "check-email" && (
                                        <Link
                                            className="button button-secondary full"
                                            to="/auth/resend-verification"
                                        >
                                            Отправить письмо ещё раз
                                        </Link>
                                    )}
                                    <Link
                                        className="button button-primary full"
                                        to={
                                            kind === "deactivated"
                                                ? "/"
                                                : "/auth/login"
                                        }
                                    >
                                        {kind === "deactivated"
                                            ? "Вернуться в ленту"
                                            : "Войти в аккаунт"}
                                        <ArrowRight size={16} />
                                    </Link>
                                </>
                            ) : (
                                <form onSubmit={submit} noValidate>
                                    {kind === "register" && (
                                        <Field
                                            label="Как вас зовут"
                                            name="auth-displayName"
                                            hint="Необязательно. Можно добавить позже."
                                            error={errors.displayName}
                                        >
                                            <input
                                                id="auth-displayName"
                                                autoComplete="nickname"
                                                value={displayName}
                                                onChange={(e) =>
                                                    setDisplayName(
                                                        e.target.value,
                                                    )
                                                }
                                                placeholder="Ваше имя"
                                            />
                                        </Field>
                                    )}
                                    {needsEmail && (
                                        <Field
                                            label="Электронная почта"
                                            name="auth-email"
                                            required
                                            error={errors.email}
                                        >
                                            <input
                                                id="auth-email"
                                                type="email"
                                                autoComplete="username"
                                                value={email}
                                                onChange={(e) =>
                                                    setEmail(e.target.value)
                                                }
                                                placeholder="you@example.com"
                                                aria-invalid={!!errors.email}
                                            />
                                        </Field>
                                    )}
                                    {hasPassword && (
                                        <Field
                                            label={
                                                kind === "login"
                                                    ? "Пароль"
                                                    : "Новый пароль"
                                            }
                                            name="auth-password"
                                            required
                                            error={errors.password}
                                            hint={
                                                newPassword
                                                    ? "От 15 символов. Длинная фраза — отличная идея."
                                                    : undefined
                                            }
                                        >
                                            <PasswordInput
                                                id="auth-password"
                                                value={password}
                                                onChange={setPassword}
                                                autoComplete={
                                                    kind === "login"
                                                        ? "current-password"
                                                        : "new-password"
                                                }
                                                error={!!errors.password}
                                                placeholder={
                                                    kind === "login"
                                                        ? "Ваш пароль"
                                                        : "Например, длинная любимая фраза"
                                                }
                                            />
                                        </Field>
                                    )}
                                    {newPassword && (
                                        <>
                                            <div className="password-hints">
                                                <span
                                                    className={
                                                        password.normalize(
                                                            "NFKC",
                                                        ).length >= 15
                                                            ? "met"
                                                            : ""
                                                    }
                                                >
                                                    <Check size={13} />
                                                    Не меньше 15 символов
                                                </span>
                                                <span>
                                                    Без обязательных цифр и
                                                    спецсимволов
                                                </span>
                                            </div>
                                            <Field
                                                label="Повторите пароль"
                                                name="auth-confirmation"
                                                required
                                                error={errors.confirmation}
                                            >
                                                <PasswordInput
                                                    id="auth-confirmation"
                                                    value={confirmation}
                                                    onChange={setConfirmation}
                                                    error={
                                                        !!errors.confirmation
                                                    }
                                                    placeholder="Ещё раз, чтобы не ошибиться"
                                                />
                                            </Field>
                                        </>
                                    )}
                                    {kind === "login" && (
                                        <Link
                                            className="forgot-link"
                                            to="/auth/forgot-password"
                                        >
                                            Не помните пароль?
                                        </Link>
                                    )}
                                    {error && (
                                        <Alert tone="error">{error}</Alert>
                                    )}
                                    <Button
                                        type="submit"
                                        className="full auth-submit"
                                        busy={busy}
                                        disabled={cooldown > 0}
                                    >
                                        {cooldown
                                            ? `Повторить через ${cooldown} с`
                                            : kind === "login"
                                              ? `Войти в ${brand.shortName}`
                                              : kind === "register"
                                                ? "Создать аккаунт"
                                                : kind === "verify-email"
                                                  ? "Подтвердить почту"
                                                  : kind ===
                                                      "resend-verification"
                                                    ? "Отправить письмо"
                                                    : kind === "forgot-password"
                                                      ? "Получить ссылку"
                                                      : kind ===
                                                          "accept-invitation"
                                                        ? "Принять приглашение"
                                                        : "Сохранить новый пароль"}
                                        <ArrowRight size={17} />
                                    </Button>
                                    {kind === "register" && (
                                        <p className="auth-disclaimer">
                                            Мы бережно относимся к вашему
                                            вниманию.
                                            <br />
                                            Никаких рекламных рассылок от
                                            прототипа.
                                        </p>
                                    )}
                                </form>
                            )}
                            {kind === "login" && (
                                <>
                                    <p className="auth-switch">
                                        Пока нет аккаунта?{" "}
                                        <Link to="/auth/register">
                                            Присоединиться{" "}
                                            <ArrowUpRight size={13} />
                                        </Link>
                                    </p>
                                    <Link
                                        className="verification-link"
                                        to="/auth/resend-verification"
                                    >
                                        Письмо подтверждения не пришло?
                                    </Link>
                                </>
                            )}
                            {kind === "register" && (
                                <p className="auth-switch">
                                    Уже знакомы?{" "}
                                    <Link to="/auth/login">Войти</Link>
                                </p>
                            )}
                            {[
                                "forgot-password",
                                "resend-verification",
                            ].includes(kind) && (
                                <Link
                                    className="text-link auth-return"
                                    to="/auth/login"
                                >
                                    <ArrowLeft size={15} />
                                    Вернуться ко входу
                                </Link>
                            )}
                        </>
                    )}
                </div>
                <span className="auth-footer">
                    {brand.name} · {brand.tagline}
                </span>
            </section>
        </main>
    );
}

export function Profile() {
    const store = useMock();
    const [name, setName] = useState(store.displayName);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const save = async (e: React.FormEvent) => {
        e.preventDefault();
        setError("");
        if ([...name.trim()].length > 120) {
            setError("Имя должно быть не длиннее 120 символов.");
            return;
        }
        setBusy(true);
        try {
            await delay();
            store.assertAvailable();
            store.setDisplayName(name.trim());
            store.toast("Профиль обновлён");
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    return (
        <>
            <div className="page-top">
                <div>
                    <span className="eyebrow">РАДЫ ЗНАКОМСТВУ</span>
                    <h1>
                        Мой профиль<span className="heading-dot">.</span>
                    </h1>
                    <p>Имя, почта и права вашего аккаунта.</p>
                </div>
            </div>
            <ScenarioState>
                <div className="settings-card">
                    <div className="profile-summary">
                        <span className="profile-avatar">
                            {store.displayName[0] || "А"}
                        </span>
                        <div>
                            <h2>{store.displayName || "Ваш аккаунт"}</h2>
                            <span>В сообществе с 15 октября 2026</span>
                        </div>
                        <Badge tone="success">Аккаунт активен</Badge>
                    </div>
                    <form onSubmit={save}>
                        <Field
                            label="Имя для отображения"
                            name="profile-name"
                            hint="До 120 символов. Оставьте пустым, чтобы убрать имя."
                        >
                            <input
                                id="profile-name"
                                value={name}
                                onChange={(e) => setName(e.target.value)}
                                autoComplete="nickname"
                            />
                        </Field>
                        <Field
                            label="Электронная почта"
                            name="profile-email"
                            hint="Адрес подтверждён. Изменить email в текущей версии нельзя."
                        >
                            <div className="verified-input">
                                <input
                                    id="profile-email"
                                    type="email"
                                    readOnly
                                    value="alex@example.com"
                                />
                                <CheckCircle2 size={18} />
                            </div>
                        </Field>
                        {error && <Alert tone="error">{error}</Alert>}
                        <div className="settings-actions">
                            <Button type="submit" busy={busy}>
                                Сохранить изменения
                            </Button>
                        </div>
                    </form>
                </div>
                <div className="settings-card role-card">
                    <ShieldCheck size={24} />
                    <div>
                        <h3>Ваши возможности в журнале</h3>
                        <div className="role-badges">
                            {store.roles.map((role) => (
                                <Badge key={role}>
                                    {
                                        {
                                            Reader: "Читатель",
                                            Author: "Автор",
                                            Moderator: "Модератор",
                                            Admin: "Администратор",
                                        }[role]
                                    }
                                </Badge>
                            ))}
                        </div>
                        <p>
                            Права назначает оператор проекта. Изменить роль
                            самостоятельно нельзя.
                        </p>
                    </div>
                </div>
            </ScenarioState>
        </>
    );
}

export function Security() {
    const store = useMock();
    const navigate = useNavigate();
    const [current, setCurrent] = useState("");
    const [next, setNext] = useState("");
    const [confirm, setConfirm] = useState("");
    const [error, setError] = useState("");
    const [busy, setBusy] = useState(false);
    const [done, setDone] = useState(false);
    const submit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError("");
        if (!current) {
            setError("Введите текущий пароль.");
            return;
        }
        const policy = passwordError(next);
        if (policy) {
            setError(policy);
            return;
        }
        if (next !== confirm) {
            setError("Новые пароли должны совпадать.");
            return;
        }
        setBusy(true);
        try {
            await delay();
            store.assertAvailable();
            if (store.scenario === "invalid")
                throw new Error("Не удалось подтвердить текущий пароль.");
            store.setSessions([]);
            setDone(true);
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    return (
        <>
            <div className="page-top">
                <div>
                    <span className="eyebrow">СПОКОЙСТВИЕ В ДЕТАЛЯХ</span>
                    <h1>
                        Безопасность<span className="heading-dot">.</span>
                    </h1>
                    <p>Хороший пароль помогает сохранить личное личным.</p>
                </div>
            </div>
            <div className="settings-card">
                <h2>Изменить пароль</h2>
                <p className="muted">
                    После изменения все сеансы, включая текущий, завершатся.
                    Понадобится войти снова.
                </p>
                <form onSubmit={submit}>
                    <Field label="Текущий пароль" name="current-password">
                        <PasswordInput
                            id="current-password"
                            value={current}
                            onChange={setCurrent}
                            autoComplete="current-password"
                            placeholder="Ваш текущий пароль"
                        />
                    </Field>
                    <Field
                        label="Новый пароль"
                        name="new-password"
                        hint="15–128 символов. Пробелы разрешены, убирать их не нужно."
                    >
                        <PasswordInput
                            id="new-password"
                            value={next}
                            onChange={setNext}
                        />
                    </Field>
                    <Field
                        label="Повторите новый пароль"
                        name="confirm-password"
                    >
                        <PasswordInput
                            id="confirm-password"
                            value={confirm}
                            onChange={setConfirm}
                        />
                    </Field>
                    {error && <Alert tone="error">{error}</Alert>}
                    <Button type="submit" busy={busy}>
                        Изменить пароль <ArrowRight size={16} />
                    </Button>
                </form>
            </div>
            <Modal
                open={done}
                onClose={() => {
                    store.setPersona("guest");
                    navigate("/auth/login");
                }}
                title="Пароль изменён"
            >
                <Alert tone="success">
                    Все предыдущие сеансы завершены. Войдите с новым паролем.
                </Alert>
                <div className="modal-footer">
                    <Button
                        onClick={() => {
                            store.setPersona("guest");
                            navigate("/auth/login");
                        }}
                    >
                        Перейти ко входу
                    </Button>
                </div>
            </Modal>
        </>
    );
}

export function Sessions() {
    const store = useMock();
    const navigate = useNavigate();
    const [selected, setSelected] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const revoke = async () => {
        setBusy(true);
        setError("");
        try {
            await delay();
            store.assertAvailable();
            const current =
                selected === "all" ||
                store.sessions.find((s) => s.id === selected)?.isCurrent;
            store.setSessions((xs) =>
                selected === "all" ? [] : xs.filter((s) => s.id !== selected),
            );
            setSelected(null);
            if (current) {
                store.setPersona("guest");
                navigate("/auth/session-expired");
            } else store.toast("Сеанс завершён");
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    return (
        <>
            <div className="page-top">
                <div>
                    <span className="eyebrow">ВЫ КОНТРОЛИРУЕТЕ ДОСТУП</span>
                    <h1>
                        Активные сеансы<span className="heading-dot">.</span>
                    </h1>
                    <p>Устройства, на которых выполнен вход в ваш аккаунт.</p>
                </div>
            </div>
            <ScenarioState
                emptyTitle="Нет активных сеансов"
                emptyText="После следующего входа устройство появится здесь."
            >
                <div className="session-list">
                    {store.sessions.length ? (
                        store.sessions.map((s) => (
                            <div className="session-card" key={s.id}>
                                <span className="device-icon">
                                    {s.userAgent.includes("iPhone") ? (
                                        <Smartphone size={25} />
                                    ) : (
                                        <Monitor size={25} />
                                    )}
                                </span>
                                <div>
                                    <h3>
                                        {s.userAgent}
                                        {s.isCurrent && (
                                            <Badge tone="success">
                                                Этот сеанс
                                            </Badge>
                                        )}
                                    </h3>
                                    <p>
                                        Последняя активность:{" "}
                                        {formatDate(s.lastUsedAt)} ·{" "}
                                        {brand.shortName}
                                    </p>
                                    <span>
                                        Создан {formatDate(s.createdAt)} ·
                                        Действует до{" "}
                                        {formatDate(s.absoluteExpiresAt)}
                                    </span>
                                </div>
                                <button
                                    className="icon-button"
                                    aria-label={`Завершить ${s.userAgent}`}
                                    onClick={() => {
                                        setSelected(s.id);
                                        setError("");
                                    }}
                                >
                                    <X size={18} />
                                </button>
                            </div>
                        ))
                    ) : (
                        <EmptyState title="Список пуст" />
                    )}
                </div>
                <div className="settings-card">
                    <h3>Нужно выйти на всех устройствах?</h3>
                    <p className="muted">
                        Будут завершены все сеансы аккаунта, включая этот. Для
                        продолжения потребуется новый вход.
                    </p>
                    <Button
                        variant="secondary"
                        onClick={() => {
                            setSelected("all");
                            setError("");
                        }}
                    >
                        Завершить все сеансы <ArrowUpRight size={15} />
                    </Button>
                </div>
            </ScenarioState>
            <Modal
                open={!!selected}
                onClose={() => setSelected(null)}
                title={
                    selected === "all"
                        ? "Выйти на всех устройствах?"
                        : "Завершить этот сеанс?"
                }
            >
                <p>
                    {selected === "all"
                        ? "Все активные сеансы аккаунта во всех клиентах проекта будут отозваны. Вы также выйдете из текущего сеанса."
                        : "На выбранном устройстве потребуется повторный вход. Уже выданный токен Blog может оставаться действующим до истечения срока."}
                </p>
                {error && <Alert tone="error">{error}</Alert>}
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => setSelected(null)}
                    >
                        Отмена
                    </Button>
                    <Button variant="danger" busy={busy} onClick={revoke}>
                        Завершить сеанс{selected === "all" ? "ы" : ""}
                    </Button>
                </div>
            </Modal>
        </>
    );
}

export function Deactivate() {
    const store = useMock();
    const navigate = useNavigate();
    const [password, setPassword] = useState("");
    const [checked, setChecked] = useState(false);
    const [confirm, setConfirm] = useState(false);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const deactivate = async () => {
        setBusy(true);
        setError("");
        try {
            await delay();
            store.assertAvailable();
            if (store.scenario === "invalid")
                throw new Error("Не удалось подтвердить пароль.");
            store.setAccountActive(false);
            store.setSessions([]);
            store.setPersona("guest");
            navigate("/auth/deactivated");
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    return (
        <>
            <div className="page-top">
                <div>
                    <span className="eyebrow">ВАЖНОЕ РЕШЕНИЕ</span>
                    <h1>Деактивация аккаунта</h1>
                    <p>Перед тем как продолжить, прочитайте, что изменится.</p>
                </div>
            </div>
            <div className="settings-card">
                <Alert tone="warning" title="Это необратимое действие">
                    Восстановление аккаунта через публичный API не
                    предусмотрено. Деактивация не удаляет персональные данные.
                </Alert>
                <ul className="consequence-list">
                    <li>Вход в аккаунт станет недоступен.</li>
                    <li>Все сеансы, включая текущий, будут завершены.</li>
                    <li>
                        Email и данные аккаунта сохраняются в сервисе Identity.
                    </li>
                    <li>Это действие относится ко всему аккаунту проекта.</li>
                </ul>
                <Field label="Подтвердите пароль" name="deactivate-password">
                    <PasswordInput
                        id="deactivate-password"
                        value={password}
                        onChange={setPassword}
                        autoComplete="current-password"
                        placeholder="Текущий пароль"
                    />
                </Field>
                <label className="checkbox-label deactivation-check">
                    <input
                        type="checkbox"
                        checked={checked}
                        onChange={(e) => setChecked(e.target.checked)}
                    />
                    Я понимаю последствия и хочу деактивировать аккаунт.
                </label>
                <Button
                    variant="danger"
                    disabled={!password || !checked}
                    onClick={() => setConfirm(true)}
                >
                    Деактивировать аккаунт
                </Button>
            </div>
            <Modal
                open={confirm}
                onClose={() => setConfirm(false)}
                title="Подтвердите деактивацию"
            >
                <p>
                    Вы потеряете возможность входа в этот аккаунт. Вернуть
                    доступ через интерфейс нельзя. Продолжить?
                </p>
                {error && <Alert tone="error">{error}</Alert>}
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => setConfirm(false)}
                    >
                        Остаться в сообществе
                    </Button>
                    <Button variant="danger" busy={busy} onClick={deactivate}>
                        Да, деактивировать
                    </Button>
                </div>
            </Modal>
        </>
    );
}
