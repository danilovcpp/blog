import {
    useEffect,
    useRef,
    type ReactNode,
    type ButtonHTMLAttributes,
} from "react";
import { Link } from "react-router-dom";
import {
    ArrowLeft,
    ArrowRight,
    Check,
    CircleAlert,
    CloudOff,
    FileText,
    LoaderCircle,
    RefreshCw,
    ShieldOff,
    X,
} from "lucide-react";
import { useMock } from "../mock/store";

export function Button({
    children,
    variant = "primary",
    busy = false,
    className = "",
    ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
    variant?: "primary" | "secondary" | "ghost" | "danger";
    busy?: boolean;
}) {
    return (
        <button
            {...props}
            disabled={props.disabled || busy}
            className={`button button-${variant} ${className}`}
        >
            {busy && <LoaderCircle size={16} className="spin" />}
            {children}
        </button>
    );
}
export function Badge({
    children,
    tone = "",
}: {
    children: ReactNode;
    tone?: string;
}) {
    return <span className={`badge ${tone}`}>{children}</span>;
}
export function Alert({
    children,
    tone = "info",
    title,
}: {
    children: ReactNode;
    tone?: "info" | "warning" | "error" | "success";
    title?: string;
}) {
    return (
        <div
            className={`alert alert-${tone}`}
            role={tone === "error" ? "alert" : "status"}
        >
            <span>
                {tone === "success" ? (
                    <Check size={18} />
                ) : (
                    <CircleAlert size={18} />
                )}
            </span>
            <div>
                {title && <strong>{title}</strong>}
                <div>{children}</div>
            </div>
        </div>
    );
}
export function Modal({
    open,
    title,
    children,
    onClose,
    wide = false,
}: {
    open: boolean;
    title: string;
    children: ReactNode;
    onClose: () => void;
    wide?: boolean;
}) {
    const ref = useRef<HTMLDivElement>(null);
    const closeRef = useRef(onClose);
    closeRef.current = onClose;
    useEffect(() => {
        if (!open) return;
        const prior = document.activeElement as HTMLElement | null;
        const overflow = document.body.style.overflow;
        document.body.style.overflow = "hidden";
        const dialog = ref.current;
        const focusables = () => [
            ...(dialog?.querySelectorAll<HTMLElement>(
                'button:not(:disabled),a[href],input,textarea,select,[tabindex="0"]',
            ) || []),
        ];
        focusables()[0]?.focus();
        const keydown = (event: KeyboardEvent) => {
            if (event.key === "Escape") closeRef.current();
            if (event.key === "Tab") {
                const nodes = focusables(),
                    first = nodes[0],
                    last = nodes.at(-1);
                if (event.shiftKey && document.activeElement === first) {
                    event.preventDefault();
                    last?.focus();
                } else if (!event.shiftKey && document.activeElement === last) {
                    event.preventDefault();
                    first?.focus();
                }
            }
        };
        document.addEventListener("keydown", keydown);
        return () => {
            document.body.style.overflow = overflow;
            document.removeEventListener("keydown", keydown);
            prior?.focus();
        };
    }, [open]);
    if (!open) return null;
    return (
        <div
            className="modal-backdrop"
            onMouseDown={(e) => {
                if (e.target === e.currentTarget) onClose();
            }}
        >
            <div
                ref={ref}
                className={`modal ${wide ? "modal-wide" : ""}`}
                role="dialog"
                aria-modal="true"
                aria-label={title}
            >
                <div className="modal-heading">
                    <h2>{title}</h2>
                    <button
                        className="icon-button"
                        onClick={onClose}
                        aria-label="Закрыть диалог"
                    >
                        <X size={20} />
                    </button>
                </div>
                {children}
            </div>
        </div>
    );
}
export function Field({
    label,
    name,
    children,
    error,
    hint,
    required = false,
}: {
    label: string;
    name: string;
    children: ReactNode;
    error?: string;
    hint?: string;
    required?: boolean;
}) {
    return (
        <div className={`field ${error ? "field-invalid" : ""}`}>
            <label htmlFor={name}>
                {label}
                {required && <span className="required"> *</span>}
            </label>
            {children}
            {error && (
                <span id={`${name}-error`} className="field-error" role="alert">
                    {error}
                </span>
            )}
            {hint && (
                <span id={`${name}-hint`} className="field-hint">
                    {hint}
                </span>
            )}
        </div>
    );
}
export function EmptyState({
    title = "Здесь пока тихо",
    text = "Материалы появятся чуть позже. А пока можно заглянуть в другие темы.",
    children,
    icon = <FileText size={28} />,
}: {
    title?: string;
    text?: string;
    children?: ReactNode;
    icon?: ReactNode;
}) {
    return (
        <div className="empty-state">
            <span className="empty-icon">{icon}</span>
            <h3>{title}</h3>
            <p>{text}</p>
            {children}
        </div>
    );
}
export function Skeleton({ rows = 3 }: { rows?: number }) {
    return (
        <div
            className="skeleton-grid"
            role="status"
            aria-label="Загрузка материалов"
        >
            {Array.from({ length: rows }, (_, i) => (
                <div key={i} className="skeleton-card">
                    <div className="skeleton skeleton-image" />
                    <div className="skeleton skeleton-line" />
                    <div className="skeleton skeleton-line short" />
                </div>
            ))}
        </div>
    );
}
export function ScenarioState({
    children,
    emptyTitle,
    emptyText,
}: {
    children: ReactNode;
    emptyTitle?: string;
    emptyText?: string;
}) {
    const { scenario, setScenario } = useMock();
    if (scenario === "loading") return <Skeleton />;
    if (scenario === "empty")
        return <EmptyState title={emptyTitle} text={emptyText} />;
    if (scenario === "offline" || scenario === "unavailable")
        return (
            <EmptyState
                icon={
                    scenario === "offline" ? (
                        <CloudOff size={30} />
                    ) : (
                        <RefreshCw size={30} />
                    )
                }
                title={
                    scenario === "offline"
                        ? "Кажется, связь потерялась"
                        : "Небольшая пауза"
                }
                text={
                    scenario === "offline"
                        ? "Проверьте подключение к интернету. Мы сохраним контекст страницы."
                        : "Сервис временно недоступен. Попробуйте вернуться через пару минут."
                }
            >
                <Button
                    variant="secondary"
                    onClick={() => setScenario("normal")}
                >
                    <RefreshCw size={16} />
                    Попробовать снова
                </Button>
            </EmptyState>
        );
    return <>{children}</>;
}
export function Guard({
    children,
    role,
}: {
    children: ReactNode;
    role?: "Author" | "Moderator" | "Admin";
}) {
    const { authed, hasRole } = useMock();
    if (!authed)
        return (
            <main className="container guard-page">
                <EmptyState
                    icon={<ShieldOff size={28} />}
                    title="Давайте познакомимся"
                    text="Для этой страницы нужно войти в аккаунт."
                >
                    <Link
                        className="button button-primary"
                        to={`/auth/login?returnTo=${encodeURIComponent(location.pathname)}`}
                    >
                        Войти <ArrowRight size={16} />
                    </Link>
                </EmptyState>
            </main>
        );
    if (role && !hasRole(role))
        return (
            <main className="container guard-page">
                <EmptyState
                    icon={<ShieldOff size={28} />}
                    title="Эта страница доступна другой роли"
                    text="Вашему аккаунту пока не предоставлен доступ к этому разделу. Права назначает оператор проекта."
                >
                    <Link className="button button-secondary" to="/">
                        <ArrowLeft size={16} />
                        Вернуться в журнал
                    </Link>
                </EmptyState>
            </main>
        );
    return <>{children}</>;
}
export function Toasts() {
    const { toasts } = useMock();
    return (
        <div className="toasts" aria-live="polite">
            {toasts.map((t) => (
                <div
                    key={t.id}
                    className={`toast ${t.tone === "error" ? "toast-error" : ""}`}
                >
                    {t.tone === "error" ? (
                        <CircleAlert size={18} />
                    ) : (
                        <Check size={18} />
                    )}{" "}
                    {t.text}
                </div>
            ))}
        </div>
    );
}
