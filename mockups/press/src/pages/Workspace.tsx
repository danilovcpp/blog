import { useEffect, useRef, useState } from "react";
import { Link, useBlocker, useNavigate, useParams } from "react-router-dom";
import {
    ArrowLeft,
    ArrowRight,
    ArrowUpRight,
    Check,
    ChevronDown,
    CircleCheck,
    Copy,
    ExternalLink,
    FileText,
    Image,
    LockKeyhole,
    MoreHorizontal,
    PanelRightClose,
    PencilLine,
    Plus,
    Save,
    Send,
    Settings2,
    Trash2,
} from "lucide-react";
import { useMock } from "../mock/store";
import { validSlug } from "../mock/rules";
import { localId, uid, formatDate, type Post } from "../mock/data";
import {
    Alert,
    Badge,
    Button,
    EmptyState,
    Field,
    Modal,
    ScenarioState,
} from "../components/ui";
import { Markdown } from "./Article";
import { NotFound } from "./Journal";

export function Workspace() {
    const store = useMock();
    const [filter, setFilter] = useState("all");
    const [deleting, setDeleting] = useState<Post | null>(null);
    const [busy, setBusy] = useState(false);
    const posts = store.posts
        .filter(
            (p) =>
                !p.deleted &&
                (p.author.id === localId || store.hasRole("Admin")),
        )
        .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
    const visible = posts.filter((p) => filter === "all" || p.state === filter);
    const remove = async () => {
        if (!deleting) return;
        setBusy(true);
        try {
            await store.mutatePost(deleting, "delete");
            store.toast(
                "Материал удалён. Его адрес остаётся зарезервированным.",
            );
            setDeleting(null);
        } catch (e) {
            store.toast(
                (e as Error).message === "concurrency_conflict"
                    ? "Версия изменилась. Обновите материал перед удалением."
                    : (e as Error).message,
                "error",
            );
        } finally {
            setBusy(false);
        }
    };
    return (
        <>
            <div className="page-top">
                <div>
                    <span className="eyebrow">ОТ МЫСЛИ К ИСТОРИИ</span>
                    <h1>
                        Мои материалы<span className="heading-dot">.</span>
                    </h1>
                    <p>
                        {store.hasRole("Admin")
                            ? "Все материалы редакции в одном месте."
                            : "Место, где ваши идеи обретают форму."}
                    </p>
                </div>
                <Link className="button button-primary" to="/workspace/new">
                    <Plus size={17} />
                    Новый материал
                </Link>
            </div>
            <div className="workspace-summary">
                <div>
                    <span>Загружено материалов</span>
                    <strong>{posts.length.toString().padStart(2, "0")}</strong>
                </div>
                <div>
                    <span>Опубликовано</span>
                    <strong>
                        {posts
                            .filter((p) => p.state === "Published")
                            .length.toString()
                            .padStart(2, "0")}
                    </strong>
                </div>
                <div>
                    <span>В работе</span>
                    <strong>
                        {posts
                            .filter((p) => p.state === "Draft")
                            .length.toString()
                            .padStart(2, "0")}
                    </strong>
                </div>
            </div>
            <div
                className="tabs"
                role="tablist"
                aria-label="Состояние материала"
            >
                {[
                    ["all", "Все материалы"],
                    ["Draft", "Черновики"],
                    ["Published", "Опубликованные"],
                ].map(([value, label]) => (
                    <button
                        key={value}
                        role="tab"
                        aria-selected={filter === value}
                        className={filter === value ? "active" : ""}
                        onClick={() => setFilter(value)}
                    >
                        {label}
                        <span>
                            {
                                posts.filter(
                                    (p) => value === "all" || p.state === value,
                                ).length
                            }
                        </span>
                    </button>
                ))}
            </div>
            <ScenarioState
                emptyTitle="У любой истории есть начало"
                emptyText="Создайте первый черновик — он будет виден только вам и администратору."
            >
                {visible.length ? (
                    <div className="workspace-list">
                        {visible.map((post) => (
                            <article className="workspace-row" key={post.id}>
                                <Link
                                    to={`/workspace/${post.id}/edit`}
                                    className="workspace-thumbnail"
                                >
                                    <img src={post.coverUrl} alt="" />
                                </Link>
                                <div className="workspace-row-content">
                                    <div className="row-badges">
                                        <Badge
                                            tone={
                                                post.state === "Published"
                                                    ? "success"
                                                    : ""
                                            }
                                        >
                                            {post.state === "Published"
                                                ? "Опубликован"
                                                : "Черновик"}
                                        </Badge>
                                        {post.state === "Draft" &&
                                            post.firstPublishedAt && (
                                                <span className="tiny muted">
                                                    Снят с публикации
                                                </span>
                                            )}
                                    </div>
                                    <h3>
                                        <Link to={`/workspace/${post.id}/edit`}>
                                            {post.title}
                                        </Link>
                                    </h3>
                                    <span className="row-date">
                                        Изменён {formatDate(post.updatedAt)} ·{" "}
                                        {post.author.id === localId
                                            ? "Ваш материал"
                                            : `Автор ${post.author.id.slice(-4)}`}
                                    </span>
                                </div>
                                <div className="workspace-row-actions">
                                    <Link
                                        className="icon-button"
                                        title="Открыть редактор"
                                        aria-label={`Редактировать «${post.title}»`}
                                        to={`/workspace/${post.id}/edit`}
                                    >
                                        <PencilLine size={17} />
                                    </Link>
                                    {post.state === "Published" && (
                                        <Link
                                            className="icon-button"
                                            aria-label="Открыть публикацию"
                                            to={`/posts/${post.slug}`}
                                        >
                                            <ArrowUpRight size={18} />
                                        </Link>
                                    )}
                                    <button
                                        className="icon-button"
                                        aria-label={`Удалить «${post.title}»`}
                                        onClick={() => setDeleting(post)}
                                    >
                                        <Trash2 size={16} />
                                    </button>
                                </div>
                            </article>
                        ))}
                    </div>
                ) : (
                    <EmptyState
                        title="В этой папке пока пусто"
                        text="Новый материал начинается с небольшой идеи."
                    >
                        <Link
                            className="button button-primary"
                            to="/workspace/new"
                        >
                            <Plus size={16} />
                            Создать черновик
                        </Link>
                    </EmptyState>
                )}
            </ScenarioState>
            <Modal
                open={!!deleting}
                onClose={() => setDeleting(null)}
                title="Удалить материал навсегда?"
            >
                <p>
                    «{deleting?.title}» исчезнет из журнала и редакции.
                    Комментарии и реакции станут недоступны. Восстановить
                    материал или повторно использовать его адрес нельзя.
                </p>
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => setDeleting(null)}
                    >
                        Оставить
                    </Button>
                    <Button variant="danger" busy={busy} onClick={remove}>
                        Удалить материал
                    </Button>
                </div>
            </Modal>
        </>
    );
}

export function Editor() {
    const { id } = useParams();
    const store = useMock();
    const navigate = useNavigate();
    const existing = store.posts.find((p) => p.id === id && !p.deleted);
    const initial = useRef<Post>(
        existing || {
            id: uid(),
            title: "",
            slug: "",
            description: "",
            markdown: "",
            coverUrl: "",
            author: { id: localId },
            categoryIds: [],
            tagIds: [],
            state: "Draft",
            version: uid(),
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            firstPublishedAt: null,
            likeCount: 0,
            viewCount: 0,
            likedBy: [],
        },
    );
    const [draft, setDraft] = useState<Post>(initial.current);
    const [dirty, setDirty] = useState(false);
    const [mode, setMode] = useState<"write" | "preview">("write");
    const [inspector, setInspector] = useState(true);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
    const [confirm, setConfirm] = useState<
        "publish" | "unpublish" | "delete" | "leave" | null
    >(null);
    const [conflict, setConflict] = useState(false);
    const [savedAt, setSavedAt] = useState<string | null>(null);
    const dirtyRef = useRef(false);
    const blocker = useBlocker(() => dirtyRef.current);
    useEffect(() => {
        if (blocker.state === "blocked") setConfirm("leave");
    }, [blocker.state]);
    const closeConfirm = () => {
        setConfirm(null);
        if (blocker.state === "blocked") blocker.reset();
    };
    useEffect(() => {
        const before = (e: BeforeUnloadEvent) => {
            if (dirty) {
                e.preventDefault();
                e.returnValue = "";
            }
        };
        addEventListener("beforeunload", before);
        return () => removeEventListener("beforeunload", before);
    }, [dirty]);
    if (id && !existing) return <NotFound />;
    if (existing && existing.author.id !== localId && !store.hasRole("Admin"))
        return (
            <NotFound
                code="403"
                title="Этот материал принадлежит другому автору"
                text="Вы можете редактировать собственные публикации."
            />
        );
    const change = <K extends keyof Post>(key: K, value: Post[K]) => {
        setDraft((d) => ({ ...d, [key]: value }));
        setDirty(true);
        dirtyRef.current = true;
        setFieldErrors((e) => ({ ...e, [key]: "" }));
    };
    const validate = () => {
        const errors: Record<string, string> = {};
        if (!draft.title.trim() || [...draft.title.trim()].length > 200)
            errors.title = "Заголовок: от 1 до 200 символов.";
        if (!validSlug(draft.slug))
            errors.slug =
                "Латиница в нижнем регистре, цифры и одиночные дефисы. До 120 символов.";
        if ([...draft.description].length > 500)
            errors.description = "Не больше 500 символов.";
        if (new TextEncoder().encode(draft.markdown).length > 1048576)
            errors.markdown = "Текст превышает 1 MiB UTF-8.";
        if (draft.coverUrl && !draft.coverUrl.startsWith("/images/")) {
            try {
                const url = new URL(draft.coverUrl);
                if (url.protocol !== "https:" || draft.coverUrl.length > 2048)
                    throw 0;
            } catch {
                errors.coverUrl =
                    "Нужна абсолютная HTTPS-ссылка (до 2048 символов).";
            }
        }
        setFieldErrors(errors);
        return Object.keys(errors).length === 0;
    };
    const save = async (
        action: "save" | "publish" | "unpublish" | "delete" = "save",
    ) => {
        if (action !== "delete" && !validate()) {
            setError(
                "Проверьте отмеченные поля. Введённый текст сохранён в форме.",
            );
            setConfirm(null);
            setInspector(true);
            return;
        }
        setBusy(true);
        setError("");
        try {
            const result = await store.mutatePost(
                { ...draft, title: draft.title.trim() },
                action,
            );
            setDirty(false);
            dirtyRef.current = false;
            setDraft(result);
            setSavedAt(
                new Date().toLocaleTimeString("ru", {
                    hour: "2-digit",
                    minute: "2-digit",
                }),
            );
            setConfirm(null);
            if (action === "delete") {
                store.toast("Материал удалён");
                navigate("/workspace");
            } else {
                store.toast(
                    action === "publish"
                        ? "Материал опубликован"
                        : action === "unpublish"
                          ? "Материал снят с публикации"
                          : "Черновик сохранён",
                );
                if (!id)
                    navigate(`/workspace/${result.id}/edit`, { replace: true });
            }
        } catch (e) {
            if ((e as Error).message === "concurrency_conflict") {
                setConflict(true);
                setConfirm(null);
            } else {
                setError((e as Error).message);
                setConfirm(null);
            }
        } finally {
            setBusy(false);
        }
    };
    const toggleId = (key: "categoryIds" | "tagIds", value: string) => {
        const arr = draft[key];
        if (
            !arr.includes(value) &&
            arr.length >= (key === "categoryIds" ? 5 : 20)
        )
            return;
        change(
            key,
            arr.includes(value)
                ? arr.filter((x) => x !== value)
                : [...arr, value],
        );
    };
    return (
        <div className="editor-page">
            <div className="editor-toolbar">
                <button
                    className="text-link"
                    onClick={() =>
                        dirty ? setConfirm("leave") : navigate("/workspace")
                    }
                >
                    <ArrowLeft size={16} />
                    Материалы
                </button>
                <span className="save-indicator">
                    {dirty ? (
                        <>
                            <span className="unsaved-dot" />
                            Есть изменения
                        </>
                    ) : savedAt ? (
                        <>
                            <Check size={14} />
                            Сохранено в {savedAt}
                        </>
                    ) : id ? (
                        "Все изменения сохранены"
                    ) : (
                        "Новый черновик"
                    )}
                </span>
                <div className="editor-toolbar-actions">
                    <Button
                        variant="secondary"
                        busy={busy}
                        onClick={() => save()}
                    >
                        <Save size={15} />
                        Сохранить
                    </Button>
                    <Button
                        busy={busy}
                        onClick={() =>
                            setConfirm(
                                draft.state === "Published"
                                    ? "unpublish"
                                    : "publish",
                            )
                        }
                    >
                        {draft.state === "Published"
                            ? "Снять с публикации"
                            : "Опубликовать"}
                        <ArrowUpRight size={15} />
                    </Button>
                </div>
            </div>
            {error && <Alert tone="error">{error}</Alert>}
            {draft.state === "Published" && (
                <Alert tone="warning">
                    Материал опубликован. Сохранение сразу изменит публичный
                    текст — отдельной черновой редакции нет.
                </Alert>
            )}
            <div className="editor-workspace">
                <section className="editor-canvas">
                    <div className="editor-canvas-top">
                        <Badge
                            tone={draft.state === "Published" ? "success" : ""}
                        >
                            {draft.state === "Published"
                                ? "Опубликован"
                                : "Черновик"}
                        </Badge>
                        <div className="segmented">
                            <button
                                className={mode === "write" ? "active" : ""}
                                onClick={() => setMode("write")}
                            >
                                Текст
                            </button>
                            <button
                                className={mode === "preview" ? "active" : ""}
                                onClick={() => setMode("preview")}
                            >
                                Предпросмотр
                            </button>
                        </div>
                        <button
                            className="icon-button"
                            aria-label="Настройки материала"
                            aria-expanded={inspector}
                            onClick={() => setInspector(!inspector)}
                        >
                            <Settings2 size={18} />
                        </button>
                    </div>
                    {mode === "write" ? (
                        <>
                            <label className="sr-only" htmlFor="post-title">
                                Заголовок материала
                            </label>
                            <textarea
                                id="post-title"
                                className="editor-title"
                                placeholder="У каждой истории есть название"
                                rows={2}
                                value={draft.title}
                                onChange={(e) =>
                                    change("title", e.target.value)
                                }
                                aria-invalid={!!fieldErrors.title}
                            />
                            {fieldErrors.title && (
                                <span className="field-error">
                                    {fieldErrors.title}
                                </span>
                            )}
                            <div className="markdown-toolbar">
                                <span>Markdown</span>
                                <button
                                    onClick={() =>
                                        change(
                                            "markdown",
                                            draft.markdown +
                                                "\n\n## Новый раздел\n",
                                        )
                                    }
                                    title="Добавить заголовок"
                                >
                                    H₂
                                </button>
                                <button
                                    onClick={() =>
                                        change(
                                            "markdown",
                                            draft.markdown +
                                                "**выделенный текст**",
                                        )
                                    }
                                    title="Жирный текст"
                                >
                                    <b>B</b>
                                </button>
                                <button
                                    onClick={() =>
                                        change(
                                            "markdown",
                                            draft.markdown + "\n\n> Цитата\n",
                                        )
                                    }
                                    title="Цитата"
                                >
                                    ❞
                                </button>
                                <span className="markdown-toolbar-hint">
                                    Пишите свободно. Форматирование — потом.
                                </span>
                            </div>
                            <label className="sr-only" htmlFor="post-markdown">
                                Текст в Markdown
                            </label>
                            <textarea
                                id="post-markdown"
                                className="editor-textarea"
                                placeholder={
                                    "Начните с мысли, которая не даёт покоя…\n\n## А дальше — расскажите историю."
                                }
                                value={draft.markdown}
                                onChange={(e) =>
                                    change("markdown", e.target.value)
                                }
                                aria-invalid={!!fieldErrors.markdown}
                            />
                        </>
                    ) : (
                        <div className="editor-preview">
                            <h1>{draft.title || "Заголовок вашей истории"}</h1>
                            {draft.coverUrl && (
                                <img
                                    src={draft.coverUrl}
                                    alt="Обложка материала"
                                />
                            )}
                            <div className="prose">
                                <Markdown
                                    value={
                                        draft.markdown ||
                                        "*Здесь появится предпросмотр вашего текста.*"
                                    }
                                />
                            </div>
                        </div>
                    )}
                    <div className="editor-bottom">
                        <span>
                            {draft.markdown.trim()
                                ? draft.markdown.trim().split(/\s+/).length
                                : 0}{" "}
                            слов
                        </span>
                        <span>
                            {Math.ceil(
                                new TextEncoder().encode(draft.markdown)
                                    .length / 1024,
                            )}{" "}
                            / 1024 KiB
                        </span>
                    </div>
                </section>
                {inspector && (
                    <aside className="editor-inspector">
                        <h3>
                            Детали истории{" "}
                            <button
                                className="icon-button"
                                aria-label="Скрыть детали"
                                onClick={() => setInspector(false)}
                            >
                                <PanelRightClose size={17} />
                            </button>
                        </h3>
                        <Field
                            label="Адрес материала"
                            name="post-slug"
                            error={fieldErrors.slug}
                            hint={
                                draft.firstPublishedAt
                                    ? "Адрес закреплён после первой публикации."
                                    : "Короткий адрес латиницей. Например, slow-morning."
                            }
                        >
                            <div className="slug-input">
                                <span>/posts/</span>
                                <input
                                    id="post-slug"
                                    value={draft.slug}
                                    disabled={!!draft.firstPublishedAt}
                                    onChange={(e) =>
                                        change("slug", e.target.value)
                                    }
                                    aria-invalid={!!fieldErrors.slug}
                                />
                                {draft.firstPublishedAt && (
                                    <LockKeyhole size={14} />
                                )}
                            </div>
                        </Field>
                        <Field
                            label="Краткое описание"
                            name="post-description"
                            error={fieldErrors.description}
                            hint={`${[...draft.description].length} / 500 символов`}
                        >
                            <textarea
                                id="post-description"
                                rows={3}
                                value={draft.description}
                                onChange={(e) =>
                                    change("description", e.target.value)
                                }
                                placeholder="О чём материал в двух предложениях"
                            />
                        </Field>
                        <Field
                            label="Обложка"
                            name="post-cover"
                            error={fieldErrors.coverUrl}
                            hint="Укажите HTTPS-ссылку на изображение. Загрузка файлов не поддерживается."
                        >
                            <input
                                id="post-cover"
                                value={draft.coverUrl}
                                onChange={(e) =>
                                    change("coverUrl", e.target.value)
                                }
                                placeholder="https://…"
                            />
                            {draft.coverUrl && (
                                <img
                                    className="inspector-cover"
                                    src={draft.coverUrl}
                                    alt="Предпросмотр обложки"
                                />
                            )}
                        </Field>
                        <div className="field">
                            <label>
                                Категории{" "}
                                <span className="field-hint">
                                    {draft.categoryIds.length} / 5
                                </span>
                            </label>
                            {store.categories.map((c) => (
                                <label className="checkbox-label" key={c.id}>
                                    <input
                                        type="checkbox"
                                        checked={draft.categoryIds.includes(
                                            c.id,
                                        )}
                                        onChange={() =>
                                            toggleId("categoryIds", c.id)
                                        }
                                    />
                                    {c.name}
                                </label>
                            ))}
                        </div>
                        <div className="field">
                            <label>
                                Теги{" "}
                                <span className="field-hint">
                                    {draft.tagIds.length} / 20
                                </span>
                            </label>
                            <div className="filter-chips">
                                {store.tags.map((t) => (
                                    <button
                                        key={t.id}
                                        className={`filter-chip ${draft.tagIds.includes(t.id) ? "active" : ""}`}
                                        onClick={() => toggleId("tagIds", t.id)}
                                    >
                                        #{t.name}
                                    </button>
                                ))}
                            </div>
                        </div>
                        {id && (
                            <button
                                className="danger-link"
                                onClick={() => setConfirm("delete")}
                            >
                                <Trash2 size={15} />
                                Удалить материал
                            </button>
                        )}
                    </aside>
                )}
            </div>
            <Modal
                open={!!confirm}
                onClose={closeConfirm}
                title={
                    confirm === "publish"
                        ? "Готово к встрече с читателями?"
                        : confirm === "unpublish"
                          ? "Снять материал с публикации?"
                          : confirm === "delete"
                            ? "Удалить материал навсегда?"
                            : "Есть несохранённые изменения"
                }
            >
                {confirm === "publish" ? (
                    <>
                        <p>
                            Мы сохраним текущие поля и опубликуем материал. Он
                            появится в журнале, а его адрес станет постоянным.
                        </p>
                        <div className="publish-summary">
                            <strong>{draft.title || "Без заголовка"}</strong>
                            <span>/posts/{draft.slug || "…"}</span>
                        </div>
                    </>
                ) : (
                    <p>
                        {confirm === "unpublish"
                            ? "Материал вернётся в черновики. Комментарии и реакции сохранятся, но станут недоступны до повторной публикации."
                            : confirm === "delete"
                              ? "Материал и обсуждение станут недоступны навсегда. Адрес останется зарезервированным. Восстановления нет."
                              : "Если уйти сейчас, изменения в форме не сохранятся. Можно остаться и сохранить черновик."}
                    </p>
                )}
                <div className="modal-footer">
                    <Button variant="secondary" onClick={closeConfirm}>
                        {confirm === "leave" ? "Остаться" : "Отмена"}
                    </Button>
                    <Button
                        variant={confirm === "delete" ? "danger" : "primary"}
                        busy={busy}
                        onClick={() => {
                            if (confirm === "leave") {
                                dirtyRef.current = false;
                                setDirty(false);
                                if (blocker.state === "blocked")
                                    blocker.proceed();
                                else navigate("/workspace");
                            } else
                                save(
                                    confirm as
                                        "publish" | "unpublish" | "delete",
                                );
                        }}
                    >
                        {confirm === "publish"
                            ? "Опубликовать"
                            : confirm === "unpublish"
                              ? "Снять с публикации"
                              : confirm === "delete"
                                ? "Удалить навсегда"
                                : "Уйти без сохранения"}
                    </Button>
                </div>
            </Modal>
            <Modal
                open={conflict}
                onClose={() => setConflict(false)}
                title="Материал изменился, пока вы работали"
                wide
            >
                <Alert tone="warning">
                    Ваш текст остался в форме. Автоматически перезаписывать
                    новую версию мы не будем.
                </Alert>
                <div className="conflict-grid">
                    <div>
                        <span className="eyebrow">ВАША ВЕРСИЯ</span>
                        <h3>{draft.title}</h3>
                        <pre>{draft.markdown.slice(0, 400)}</pre>
                    </div>
                    <div>
                        <span className="eyebrow">СЕРВЕРНАЯ ВЕРСИЯ</span>
                        <h3>{existing?.title || "Текущий материал"}</h3>
                        <pre>
                            {existing?.markdown.slice(0, 400) ||
                                "Материал ещё не создан."}
                        </pre>
                    </div>
                </div>
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => {
                            navigator.clipboard
                                ?.writeText(draft.markdown)
                                .then(() => store.toast("Ваш текст скопирован"))
                                .catch(() =>
                                    store.toast(
                                        "Выделите и скопируйте текст вручную",
                                        "error",
                                    ),
                                );
                        }}
                    >
                        <Copy size={15} />
                        Скопировать мой текст
                    </Button>
                    <Button
                        onClick={() => {
                            store.setScenario("normal");
                            setDraft((d) => ({
                                ...d,
                                version: existing?.version || d.version,
                            }));
                            setConflict(false);
                            store.toast(
                                "Актуальная версия загружена. Проверьте различия перед сохранением.",
                            );
                        }}
                    >
                        Вернуться к сравнению
                    </Button>
                </div>
            </Modal>
        </div>
    );
}
