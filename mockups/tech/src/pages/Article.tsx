import { brand } from "../brands";
import { useEffect, useState } from "react";
import { Link, useParams, useNavigate } from "react-router-dom";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
    Cpu,
    Clock3,
    Wrench,
    Radio,
    ArrowLeft,
    ArrowRight,
    ArrowUpRight,
    Check,
    ChevronDown,
    LockKeyhole,
    MessageCircle,
    MoreHorizontal,
    Send,
    Trash2,
} from "lucide-react";
import { useMock, delay } from "../mock/store";
import {
    localId,
    readTime,
    shortAuthor,
    formatDate,
    type Comment,
} from "../mock/data";
import {
    Alert,
    Badge,
    Button,
    EmptyState,
    Modal,
    ScenarioState,
} from "../components/ui";
import { PostCard, NotFound } from "./Journal";
import { commentInPublicTree } from "../mock/rules";
import { tagTone } from "../themes/tones";
import { PostIcon } from "../components/PostIcon";

export function Markdown({ value }: { value: string }) {
    return (
        <ReactMarkdown
            remarkPlugins={[remarkGfm]}
            components={{
                h2: ({ children }) => (
                    <h2
                        id={String(children).toLowerCase().replace(/\s+/g, "-")}
                    >
                        {children}
                    </h2>
                ),
                a: ({ children, ...props }) => (
                    <a {...props} rel="noopener noreferrer">
                        {children}
                    </a>
                ),
            }}
        >
            {value}
        </ReactMarkdown>
    );
}

export function Article() {
    const { slug } = useParams();
    const store = useMock();
    const navigate = useNavigate();
    const [likeBusy, setLikeBusy] = useState(false);
    const [progress, setProgress] = useState(0);
    const post = store.posts.find(
        (p) => p.slug === slug && p.state === "Published" && !p.deleted,
    );
    useEffect(() => {
        if (post) store.registerView(post.id);
    }, [post?.id, store.authed]);
    useEffect(() => {
        const update = () => {
            const total = document.documentElement.scrollHeight - innerHeight;
            setProgress(total > 0 ? (scrollY / total) * 100 : 0);
        };
        window.addEventListener("scroll", update);
        update();
        if (location.hash) {
            requestAnimationFrame(() =>
                document
                    .getElementById(location.hash.slice(1))
                    ?.scrollIntoView(),
            );
        }
        return () => window.removeEventListener("scroll", update);
    }, [slug]);
    if (!post) return <NotFound />;
    const category = store.categories.find((c) =>
        post.categoryIds.includes(c.id),
    );
    const liked = store.authed && post.likedBy.includes(localId);
    const saved = store.bookmarks.includes(post.id);
    const headings = post.markdown
        .split("\n")
        .filter((l) => l.startsWith("## "))
        .map((l) => l.slice(3));
    const count = store.comments.filter(
        (c) => c.publicationId === post.id && c.state === "Visible",
    ).length;
    const like = async () => {
        if (!store.authed) {
            navigate(
                `/auth/login?returnTo=${encodeURIComponent("/posts/" + post.slug)}`,
            );
            return;
        }
        setLikeBusy(true);
        try {
            await store.toggleLike(post.id);
        } catch (e) {
            store.toast((e as Error).message, "error");
        } finally {
            setLikeBusy(false);
        }
    };
    return (
        <main className="tech-article-page container">
            <div
                className="reading-progress"
                style={{ width: `${progress}%` }}
            />
            <div className="article-breadcrumb">
                <Link to="/">Главная</Link>
                <span>›</span>
                <Link to={`/category/${category?.slug}`}>{category?.name}</Link>
                <span>›</span>
                <span>Статья</span>
            </div>
            <ScenarioState>
                <div className="tech-article-grid">
                    <article className="article-paper">
                        <header className="article-header">
                            <div className="article-label-row">
                                <Link
                                    className="category-label"
                                    to={`/category/${category?.slug}`}
                                >
                                    {category?.name}
                                </Link>
                                <span>
                                    <Clock3 size={14} />
                                    {readTime(post.markdown)} мин чтения
                                </span>
                            </div>
                            <h1>{post.title}</h1>
                            <p className="article-deck">{post.description}</p>
                            <div className="article-byline">
                                <Link
                                    className="person"
                                    to={`/authors/${post.author.id}`}
                                >
                                    <span className="person-avatar">
                                        {post.author.id === localId
                                            ? "АП"
                                            : "ИК"}
                                    </span>
                                    <strong>
                                        {shortAuthor(post.author.id)}
                                    </strong>
                                </Link>
                                <span className="author-badge">Автор</span>
                                <time>
                                    {formatDate(post.firstPublishedAt!)}
                                </time>
                                {store.hasRole("Author") &&
                                    (post.author.id === localId ||
                                        store.hasRole("Admin")) && (
                                        <Link
                                            className="text-link edit-article"
                                            to={`/workspace/${post.id}/edit`}
                                        >
                                            Редактировать{" "}
                                            <ArrowRight size={13} />
                                        </Link>
                                    )}
                            </div>
                        </header>
                        {post.coverUrl && (
                            <img
                                className="article-cover"
                                src={post.coverUrl}
                                alt={brand.article.coverAlt}
                            />
                        )}
                        <div className="article-action-bar">
                            <span aria-label={`Просмотры: ${post.viewCount}`}>
                                <PostIcon name="views" />
                                {post.viewCount.toLocaleString("ru")}
                            </span>
                            <a
                                href="#comments"
                                aria-label={`Комментарии: ${count}`}
                            >
                                <PostIcon name="comments" />
                                {count}
                            </a>
                            <button
                                className={`post-like${liked ? " is-active" : ""}`}
                                aria-label={liked ? "Убрать лайк" : "Нравится"}
                                aria-pressed={liked}
                                aria-busy={likeBusy}
                                disabled={likeBusy}
                                onClick={like}
                            >
                                <PostIcon name="heart" active={liked} />
                                {post.likeCount}
                            </button>
                            <button
                                className={saved ? "liked" : ""}
                                aria-pressed={saved}
                                onClick={() => {
                                    store.toggleBookmark(post.id);
                                    store.toast(
                                        saved
                                            ? "Удалено из закладок"
                                            : "Сохранено в закладки",
                                    );
                                }}
                            >
                                <PostIcon name="bookmark" active={saved} />
                                <span className="bookmark-word">
                                    {saved ? "Сохранено" : "В закладки"}
                                </span>
                            </button>
                            <button
                                aria-label="Скопировать ссылку"
                                onClick={async () => {
                                    try {
                                        await navigator.clipboard.writeText(
                                            location.href.split("#")[0],
                                        );
                                        store.toast("Ссылка скопирована");
                                    } catch {
                                        store.toast(
                                            "Скопируйте адрес из строки браузера",
                                            "error",
                                        );
                                    }
                                }}
                            >
                                <PostIcon name="link" />
                            </button>
                        </div>
                        <div className="article-main">
                            <div className="prose">
                                <Markdown value={post.markdown} />
                            </div>
                            <div className="article-tags">
                                {store.tags
                                    .filter((t) => post.tagIds.includes(t.id))
                                    .map((t) => (
                                        <Link
                                            key={t.id}
                                            to={`/tag/${t.slug}`}
                                            data-tone={tagTone(t.slug)}
                                        >
                                            #{t.name}
                                        </Link>
                                    ))}
                            </div>
                            <div className="article-reactions">
                                <div>
                                    <h3>{brand.article.reactionTitle}</h3>
                                    <p>{brand.article.reactionText}</p>
                                </div>
                                <Button
                                    disabled={likeBusy}
                                    aria-busy={likeBusy}
                                    variant="secondary"
                                    className={`post-like${liked ? " is-active" : ""}`}
                                    aria-pressed={liked}
                                    onClick={like}
                                >
                                    <PostIcon name="heart" active={liked} />
                                    {liked ? "Вам нравится" : "Нравится"} ·{" "}
                                    {post.likeCount}
                                </Button>
                            </div>
                            <Comments publicationId={post.id} />
                        </div>
                    </article>
                    <aside className="article-right-aside">
                        <section className="side-panel article-toc">
                            <h3>
                                <Cpu size={19} />
                                Внутри статьи
                            </h3>
                            {headings.map((h, i) => (
                                <a
                                    key={h}
                                    href={`#${h.toLowerCase().replace(/\s+/g, "-")}`}
                                >
                                    <span>
                                        {String(i + 1).padStart(2, "0")}
                                    </span>
                                    {h}
                                </a>
                            ))}
                            <a className="toc-comments" href="#comments">
                                <MessageCircle size={16} />К обсуждению ({count}
                                )
                            </a>
                        </section>
                        <section className="side-panel article-note">
                            <Radio size={27} />
                            <span className="eyebrow">
                                {brand.article.noteEyebrow}
                            </span>
                            <h3>
                                {brand.article.noteTitle[0]}
                                <br />
                                {brand.article.noteTitle[1]}
                            </h3>
                            <p>{brand.article.noteText}</p>
                        </section>
                        <section className="side-panel">
                            <div className="panel-title">
                                <h3>
                                    <Wrench size={17} />
                                    {brand.feed.relatedTitle}
                                </h3>
                            </div>
                            {store.posts
                                .filter(
                                    (p) =>
                                        p.id !== post.id &&
                                        p.state === "Published" &&
                                        !p.deleted,
                                )
                                .slice(0, 3)
                                .map((p) => (
                                    <Link
                                        className="related-mini"
                                        key={p.id}
                                        to={`/posts/${p.slug}`}
                                    >
                                        {p.coverUrl && (
                                            <img src={p.coverUrl} alt="" />
                                        )}
                                        <span>
                                            {p.title}
                                            <small>
                                                {readTime(p.markdown)} мин
                                                чтения
                                            </small>
                                        </span>
                                    </Link>
                                ))}
                        </section>
                    </aside>
                </div>
            </ScenarioState>
        </main>
    );
}
function Composer({
    publicationId,
    parent = null,
    existing,
    onDone,
    onSaved,
}: {
    publicationId: string;
    parent?: Comment | null;
    existing?: Comment;
    onDone?: () => void;
    onSaved?: () => void;
}) {
    const store = useMock();
    const [content, setContent] = useState(existing?.content || "");
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const submit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!content.trim() || [...content.trim()].length > 5000) {
            setError("Напишите от 1 до 5000 символов.");
            return;
        }
        setBusy(true);
        setError("");
        try {
            await store.saveComment(publicationId, content, parent, existing);
            setContent("");
            store.toast(
                existing ? "Комментарий обновлён" : "Спасибо за ваш отклик",
            );
            onSaved?.();
            onDone?.();
        } catch (e) {
            setError(
                (e as Error).message === "concurrency_conflict"
                    ? "Комментарий изменился. Ваш текст сохранён в форме — перечитайте актуальную версию перед повтором."
                    : (e as Error).message,
            );
        } finally {
            setBusy(false);
        }
    };
    return (
        <form className="comment-composer" onSubmit={submit}>
            {parent && (
                <span className="reply-context">
                    Ответ на комментарий ·{" "}
                    {parent.author.id === localId
                        ? "ваш"
                        : parent.author.id.slice(-4)}
                </span>
            )}
            <label
                className="sr-only"
                htmlFor={`comment-${existing?.id || parent?.id || "root"}`}
            >
                Текст комментария
            </label>
            <textarea
                id={`comment-${existing?.id || parent?.id || "root"}`}
                value={content}
                onChange={(e) => setContent(e.target.value)}
                placeholder={
                    parent
                        ? "Продолжите мысль…"
                        : "О чём вы подумали во время чтения?"
                }
                rows={3}
                aria-invalid={!!error}
            />
            {error && <Alert tone="error">{error}</Alert>}
            <div className="composer-bottom">
                <span>{[...content.trim()].length} / 5000 · Простой текст</span>
                <div>
                    {onDone && (
                        <Button variant="ghost" type="button" onClick={onDone}>
                            Отмена
                        </Button>
                    )}
                    <Button
                        busy={busy}
                        disabled={!content.trim()}
                        type="submit"
                    >
                        {existing ? "Сохранить" : "Отправить"}
                        <ArrowUpRight size={15} />
                    </Button>
                </div>
            </div>
        </form>
    );
}

export function Comments({ publicationId }: { publicationId: string }) {
    const store = useMock();
    const [limit, setLimit] = useState(3);
    const comments = store.comments.filter(
        (c) => c.publicationId === publicationId,
    );
    const includable = (c: Comment): boolean =>
        commentInPublicTree(c, comments);
    const roots = comments.filter((c) => !c.parentId && includable(c));
    return (
        <section className="comments-section" id="comments">
            <div className="comments-heading">
                <h2>
                    Обсуждение{" "}
                    <span className="heading-dot">
                        · {comments.filter((c) => c.state === "Visible").length}
                    </span>
                </h2>
                <MessageCircle size={23} />
            </div>
            <p className="comments-intro">
                Делитесь опытом, задавайте вопросы и помогайте разобраться в
                деталях.
            </p>
            {store.authed ? (
                <Composer
                    publicationId={publicationId}
                    onSaved={() => setLimit(roots.length + 1)}
                />
            ) : (
                <div className="comment-auth">
                    <span className="avatar">
                        <LockKeyhole size={18} />
                    </span>
                    <div>
                        <strong>Ваш взгляд тоже важен</strong>
                        <p>Войдите, чтобы присоединиться к разговору.</p>
                    </div>
                    <Link
                        className="button button-secondary"
                        to={`/auth/login?returnTo=/posts/${store.posts.find((p) => p.id === publicationId)?.slug}`}
                    >
                        Войти <ArrowRight size={15} />
                    </Link>
                </div>
            )}
            {roots.length ? (
                <div className="comment-list">
                    {roots.slice(0, limit).map((c) => (
                        <CommentNode
                            key={c.id}
                            comment={c}
                            all={comments}
                            includable={includable}
                        />
                    ))}
                    {limit < roots.length && (
                        <Button
                            variant="ghost"
                            onClick={() => setLimit((l) => l + 3)}
                        >
                            Больше комментариев <ChevronDown size={16} />
                        </Button>
                    )}
                </div>
            ) : (
                <EmptyState
                    title="Первое слово за вами"
                    text="Пока никто не поделился мыслями. Начните разговор."
                    icon={<MessageCircle size={25} />}
                />
            )}
        </section>
    );
}

function CommentNode({
    comment: c,
    all,
    includable,
}: {
    comment: Comment;
    all: Comment[];
    includable: (c: Comment) => boolean;
}) {
    const store = useMock();
    const [expanded, setExpanded] = useState(false);
    const [childLimit, setChildLimit] = useState(2);
    const [reply, setReply] = useState(false);
    const [edit, setEdit] = useState(false);
    const [confirm, setConfirm] = useState(false);
    const [busy, setBusy] = useState(false);
    const children = all.filter(
        (child) => child.parentId === c.id && includable(child),
    );
    const visible = c.state === "Visible";
    const own = visible && store.authed && c.author.id === localId;
    const remove = async () => {
        setBusy(true);
        try {
            await store.changeComment(c.id, "delete");
            setConfirm(false);
            store.toast("Текст комментария удалён. Ответы сохранены.");
        } catch (e) {
            store.toast((e as Error).message, "error");
        } finally {
            setBusy(false);
        }
    };
    return (
        <article
            className={`comment-node ${!visible ? "comment-placeholder" : ""}`}
        >
            <div className="comment-avatar">
                {visible ? own ? "В" : "Ч" : <MoreHorizontal size={17} />}
            </div>
            <div className="comment-content">
                <div className="comment-byline">
                    <strong>
                        {visible
                            ? own
                                ? "Вы"
                                : shortAuthor(c.author.id)
                            : c.state === "Hidden"
                              ? "Комментарий скрыт"
                              : "Комментарий удалён"}
                    </strong>
                    <span>
                        {formatDate(c.createdAt, true)}
                        {c.editedAt ? " · изменён" : ""}
                    </span>
                </div>
                {edit ? (
                    <Composer
                        publicationId={c.publicationId}
                        existing={c}
                        onDone={() => setEdit(false)}
                    />
                ) : visible ? (
                    <p className="comment-text">{c.content}</p>
                ) : (
                    <p className="placeholder-text">
                        {c.state === "Hidden"
                            ? "Скрыт модератором. Ответы остаются доступны."
                            : "Текст больше недоступен. Разговор может продолжаться."}
                    </p>
                )}
                <div className="comment-actions">
                    {visible &&
                        c.depth < 5 &&
                        (store.authed ? (
                            <button onClick={() => setReply(!reply)}>
                                Ответить
                            </button>
                        ) : (
                            <Link to="/auth/login">Ответить</Link>
                        ))}
                    {(own || (visible && store.hasRole("Admin"))) && (
                        <>
                            <button onClick={() => setEdit(!edit)}>
                                Изменить
                            </button>
                            <button onClick={() => setConfirm(true)}>
                                Удалить
                            </button>
                        </>
                    )}
                    {visible && c.depth === 5 && (
                        <span className="tiny muted">
                            Максимальная глубина обсуждения
                        </span>
                    )}
                </div>
                {reply && (
                    <Composer
                        publicationId={c.publicationId}
                        parent={c}
                        onDone={() => {
                            setReply(false);
                            setExpanded(true);
                            setChildLimit((l) => l + 2);
                        }}
                    />
                )}{" "}
                {children.length > 0 && (
                    <button
                        className="show-replies"
                        aria-expanded={expanded}
                        onClick={() => setExpanded(!expanded)}
                    >
                        <span className="reply-line" />
                        {expanded ? "Свернуть ответы" : "Показать ответы"}
                        <ChevronDown
                            size={14}
                            style={{
                                transform: expanded
                                    ? "rotate(180deg)"
                                    : undefined,
                            }}
                        />
                    </button>
                )}
                {expanded && (
                    <div className="comment-children">
                        {children.slice(0, childLimit).map((child) => (
                            <CommentNode
                                key={child.id}
                                comment={child}
                                all={all}
                                includable={includable}
                            />
                        ))}
                        {children.length > childLimit && (
                            <button
                                className="inline-button"
                                onClick={() => setChildLimit((l) => l + 2)}
                            >
                                Ещё ответы
                            </button>
                        )}
                    </div>
                )}
            </div>
            <Modal
                open={confirm}
                onClose={() => setConfirm(false)}
                title="Удалить комментарий?"
            >
                <p>
                    Текст будет удалён без возможности восстановления. Ответы
                    других участников останутся в обсуждении.
                </p>
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => setConfirm(false)}
                    >
                        Отмена
                    </Button>
                    <Button variant="danger" busy={busy} onClick={remove}>
                        <Trash2 size={16} />
                        Удалить
                    </Button>
                </div>
            </Modal>
        </article>
    );
}

export function CommentManage() {
    const { id } = useParams();
    const store = useMock();
    const [busy, setBusy] = useState(false);
    const navigate = useNavigate();
    const c = store.comments.find((c) => c.id === id);
    const post = store.posts.find(
        (p) =>
            p.id === c?.publicationId && p.state === "Published" && !p.deleted,
    );
    if (!c || !post) return <NotFound />;
    if (c.author.id !== localId && !store.hasRole("Admin"))
        return (
            <NotFound
                code="403"
                title="Это личный контекст комментария"
                text="Изменять этот комментарий может его владелец или администратор."
            />
        );
    return (
        <main className="container narrow-page">
            <Link className="breadcrumb" to={`/posts/${post.slug}`}>
                <ArrowLeft size={15} />К материалу
            </Link>
            <h1>Ваш комментарий</h1>
            <Badge tone={c.state === "Hidden" ? "warning" : ""}>
                {c.state === "Hidden"
                    ? "Скрыт"
                    : c.state === "Deleted"
                      ? "Удалён"
                      : "Виден всем"}
            </Badge>
            {c.state === "Hidden" && !store.hasRole("Admin") ? (
                <Alert tone="warning">
                    Текст скрыт модератором. Вы можете удалить комментарий, но
                    не изменить его.
                </Alert>
            ) : c.state === "Deleted" ? (
                <Alert>
                    Текст комментария удалён без возможности восстановления.
                </Alert>
            ) : (
                <Composer publicationId={c.publicationId} existing={c} />
            )}{" "}
            {c.state !== "Deleted" && (
                <Button
                    variant="danger"
                    busy={busy}
                    onClick={async () => {
                        setBusy(true);
                        try {
                            await store.changeComment(c.id, "delete");
                            store.toast("Комментарий удалён");
                            navigate(`/posts/${post.slug}`);
                        } catch (e) {
                            store.toast((e as Error).message, "error");
                        } finally {
                            setBusy(false);
                        }
                    }}
                >
                    Удалить мой комментарий
                </Button>
            )}
        </main>
    );
}
