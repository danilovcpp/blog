import type { CSSProperties, ReactNode } from "react";
import { Link } from "react-router-dom";
import {
    BookOpen,
    ChevronRight,
    FileText,
    MessageCircle,
    ShieldCheck,
    Tags,
    Users,
} from "lucide-react";
import { brand } from "../brands";
import { theme } from "../themes";
import { useMock } from "../mock/store";
import { Footer, Header, Logo } from "./Layout";
import "./auth.css";

const cornerTraces = [
    {
        points: "9,165 9,66 51,24 123,24 149,50",
        pads: [
            [9, 165],
            [149, 50],
        ],
    },
    {
        points: "24,149 24,74 59,39 116,39 138,61",
        pads: [
            [24, 149],
            [138, 61],
        ],
    },
    {
        points: "39,132 39,82 67,54 104,54",
        pads: [
            [39, 132],
            [104, 54],
        ],
    },
];
const pageTraces = [
    {
        points: "18,0 18,52 36,70 36,158 18,176 18,242",
        pads: [[18, 242]],
    },
    {
        points: "36,0 36,44 54,62 54,166 36,184 36,224",
        pads: [[36, 224]],
    },
    {
        points: "54,0 54,36 72,54 72,176 54,194 54,208",
        pads: [[54, 208]],
    },
];

export function CircuitLines({
    className = "",
    variant = "corner",
}: {
    className?: string;
    variant?: "corner" | "page";
}) {
    const traces = variant === "page" ? pageTraces : cornerTraces;
    return (
        <svg
            className={`auth-circuit ${className}`}
            viewBox={variant === "page" ? "0 0 80 260" : "0 0 180 180"}
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            {traces.map(({ points, pads }) => (
                <g key={points}>
                    <polyline points={points} />
                    {pads.map(([x, y]) => (
                        <circle
                            key={`${x},${y}`}
                            cx={x}
                            cy={y}
                            r="4"
                            fill="var(--surface)"
                        />
                    ))}
                </g>
            ))}
        </svg>
    );
}

export function AuthSecurity() {
    return (
        <details className="auth-security">
            <summary>
                <span className="auth-security-icon">
                    <ShieldCheck size={25} />
                </span>
                <span>
                    <strong>Безопасность вашего аккаунта</strong>
                    <span>Пароль и активные сеансы — под вашим контролем.</span>
                </span>
                <ChevronRight size={19} />
            </summary>
            <p>
                После входа вы сможете изменить пароль и завершить другие сеансы
                в настройках аккаунта. Никому не сообщайте свой пароль и
                проверяйте адрес сайта перед входом.
            </p>
        </details>
    );
}

export function AuthFrame({ children }: { children: ReactNode }) {
    const store = useMock();
    if (brand.presentation !== "workshop") {
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
                {children}
            </main>
        );
    }
    const icons = [FileText, MessageCircle, Tags, Users];
    const published = store.posts.filter(
        (post) => post.state === "Published" && !post.deleted,
    );
    const publicationIds = new Set(published.map((post) => post.id));
    const stats = [
        { Icon: FileText, count: published.length, label: "публикаций" },
        {
            Icon: MessageCircle,
            count: store.comments.filter(
                (comment) =>
                    comment.state === "Visible" &&
                    publicationIds.has(comment.publicationId),
            ).length,
            label: "комментариев",
        },
        {
            Icon: BookOpen,
            count: store.categories.length,
            label: "направлений",
        },
        { Icon: Tags, count: store.tags.length, label: "тем в каталоге" },
    ];
    return (
        <div className="auth-page">
            <Header />
            <main
                className="auth-scene"
                style={
                    {
                        "--auth-background": `url("${theme.auth}")`,
                    } as CSSProperties
                }
            >
                <div className="auth-stage">
                    <div className="auth-panel">
                        <CircuitLines
                            className="auth-circuit-page"
                            variant="page"
                        />
                        <aside className="auth-intro" aria-label="О сообществе">
                            <p className="auth-intro-eyebrow">
                                {brand.auth.eyebrow}
                            </p>
                            <div className="auth-brand">
                                <Logo />
                            </div>
                            <p className="auth-intro-description">
                                {brand.auth.description}
                            </p>
                            <div className="auth-feature-grid">
                                {brand.auth.features.map(
                                    ([title, description, href], index) => {
                                        const Icon = icons[index];
                                        return (
                                            <Link
                                                className={`auth-feature auth-feature-${index}`}
                                                to={href}
                                                key={title}
                                            >
                                                <span className="auth-feature-icon">
                                                    <Icon
                                                        size={29}
                                                        strokeWidth={1.7}
                                                    />
                                                </span>
                                                <span>
                                                    <strong>{title}</strong>
                                                    <span>{description}</span>
                                                </span>
                                            </Link>
                                        );
                                    },
                                )}
                            </div>
                            <dl className="auth-community-stats">
                                {stats.map(({ Icon, count, label }) => (
                                    <div key={label}>
                                        <dt>
                                            <Icon size={26} strokeWidth={1.7} />
                                            <span>{label}</span>
                                        </dt>
                                        <dd>{count}</dd>
                                    </div>
                                ))}
                            </dl>
                        </aside>
                        {children}
                    </div>
                </div>
            </main>
            <Footer />
        </div>
    );
}
