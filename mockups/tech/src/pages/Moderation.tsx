import { useState } from "react";
import { Link } from "react-router-dom";
import {
    ArrowUpRight,
    Eye,
    EyeOff,
    MessageCircle,
    Pencil,
    ShieldCheck,
    Trash2,
} from "lucide-react";
import { useMock } from "../mock/store";
import { validSlug } from "../mock/rules";
import { formatDate, shortAuthor, type Comment } from "../mock/data";
import {
    Alert,
    Badge,
    Button,
    EmptyState,
    Field,
    Modal,
    ScenarioState,
} from "../components/ui";

export function Moderation() {
    const store = useMock();
    const [state, setState] = useState("all");
    const [publication, setPublication] = useState("all");
    const [selected, setSelected] = useState<Comment | null>(null);
    const [action, setAction] = useState<"hide" | "restore" | "delete" | null>(
        null,
    );
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const publications = store.posts.filter(
        (p) => p.state === "Published" && !p.deleted,
    );
    const comments = store.comments
        .filter(
            (c) =>
                publications.some((p) => p.id === c.publicationId) &&
                (state === "all" || c.state === state) &&
                (publication === "all" || c.publicationId === publication),
        )
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    const perform = async () => {
        if (!selected || !action) return;
        setBusy(true);
        setError("");
        try {
            await store.changeComment(selected.id, action);
            store.toast(
                action === "hide"
                    ? "Комментарий скрыт. Ответы остаются доступны."
                    : action === "restore"
                      ? "Комментарий снова виден читателям"
                      : "Текст комментария удалён",
            );
            setSelected(null);
            setAction(null);
        } catch (e) {
            setError(
                (e as Error).message === "concurrency_conflict"
                    ? "Комментарий уже изменён. Обновите список перед повтором."
                    : (e as Error).message,
            );
        } finally {
            setBusy(false);
        }
    };
    return (
        <>
            <div className="page-top">
                <div>
                    <span className="eyebrow">БЕРЕЖНО К РАЗГОВОРУ</span>
                    <h1>
                        Модерация<span className="heading-dot">.</span>
                    </h1>
                    <p>Сохраняйте пространство для разных точек зрения.</p>
                </div>
                <span className="page-icon">
                    <ShieldCheck size={28} />
                </span>
            </div>
            <Alert>
                Новые комментарии видны сразу. Здесь можно скрыть текст,
                сохранив ответы, или вернуть его в обсуждение.
            </Alert>
            <div className="moderation-filters">
                <div className="tabs" role="tablist">
                    {[
                        ["all", "Все"],
                        ["Visible", "Видимые"],
                        ["Hidden", "Скрытые"],
                        ["Deleted", "Удалённые"],
                    ].map(([key, label]) => (
                        <button
                            key={key}
                            role="tab"
                            aria-selected={state === key}
                            className={state === key ? "active" : ""}
                            onClick={() => setState(key)}
                        >
                            {label}
                        </button>
                    ))}
                </div>
                <label className="sr-only" htmlFor="moderation-post">
                    Материал
                </label>
                <select
                    id="moderation-post"
                    value={publication}
                    onChange={(e) => setPublication(e.target.value)}
                >
                    <option value="all">Все опубликованные материалы</option>
                    {publications.map((p) => (
                        <option key={p.id} value={p.id}>
                            {p.title}
                        </option>
                    ))}
                </select>
            </div>
            <ScenarioState
                emptyTitle="Всё спокойно"
                emptyText="Комментариев в выбранном состоянии пока нет."
            >
                {comments.length ? (
                    <div className="moderation-list">
                        {comments.map((c) => {
                            const post = publications.find(
                                (p) => p.id === c.publicationId,
                            )!;
                            return (
                                <article className="moderation-card" key={c.id}>
                                    <div className="moderation-card-top">
                                        <span className="avatar">Ч</span>
                                        <div>
                                            <strong>
                                                {shortAuthor(c.author.id)}
                                            </strong>
                                            <span>
                                                {formatDate(c.createdAt)}
                                                {c.parentId
                                                    ? " · Ответ в обсуждении"
                                                    : ""}
                                            </span>
                                        </div>
                                        <Badge
                                            tone={
                                                c.state === "Visible"
                                                    ? "success"
                                                    : c.state === "Hidden"
                                                      ? "warning"
                                                      : ""
                                            }
                                        >
                                            {c.state === "Visible"
                                                ? "Виден"
                                                : c.state === "Hidden"
                                                  ? "Скрыт"
                                                  : "Удалён"}
                                        </Badge>
                                    </div>
                                    <p
                                        className={
                                            c.state === "Deleted"
                                                ? "muted italic"
                                                : ""
                                        }
                                    >
                                        {c.content ||
                                            "Текст удалён без возможности восстановления."}
                                    </p>
                                    <div className="moderation-card-bottom">
                                        <Link to={`/posts/${post.slug}`}>
                                            {post.title}
                                            <ArrowUpRight size={14} />
                                        </Link>
                                        <div>
                                            {c.state !== "Deleted" && (
                                                <Button
                                                    variant="secondary"
                                                    onClick={() => {
                                                        setSelected(c);
                                                        setAction(
                                                            c.state ===
                                                                "Visible"
                                                                ? "hide"
                                                                : "restore",
                                                        );
                                                    }}
                                                >
                                                    {c.state === "Visible" ? (
                                                        <EyeOff size={15} />
                                                    ) : (
                                                        <Eye size={15} />
                                                    )}{" "}
                                                    {c.state === "Visible"
                                                        ? "Скрыть"
                                                        : "Восстановить"}
                                                </Button>
                                            )}
                                            {store.hasRole("Admin") &&
                                                c.state !== "Deleted" && (
                                                    <>
                                                        <Link
                                                            className="icon-button"
                                                            aria-label="Редактировать комментарий"
                                                            to={`/comments/${c.id}/manage`}
                                                        >
                                                            <Pencil size={16} />
                                                        </Link>
                                                        <button
                                                            className="icon-button"
                                                            aria-label="Удалить комментарий"
                                                            onClick={() => {
                                                                setSelected(c);
                                                                setAction(
                                                                    "delete",
                                                                );
                                                            }}
                                                        >
                                                            <Trash2 size={16} />
                                                        </button>
                                                    </>
                                                )}
                                        </div>
                                    </div>
                                </article>
                            );
                        })}
                    </div>
                ) : (
                    <EmptyState
                        title="Всё спокойно"
                        text="В выбранной подборке нет комментариев."
                        icon={<MessageCircle size={26} />}
                    />
                )}
            </ScenarioState>
            <Modal
                open={!!selected}
                onClose={() => {
                    setSelected(null);
                    setError("");
                }}
                title={
                    action === "hide"
                        ? "Скрыть комментарий?"
                        : action === "restore"
                          ? "Вернуть комментарий в обсуждение?"
                          : "Удалить текст навсегда?"
                }
            >
                <p>
                    {action === "hide"
                        ? "Автор и текст исчезнут из публичного обсуждения. Видимые ответы останутся доступны через заглушку."
                        : action === "restore"
                          ? "Сохранённый текст и автор снова станут видны всем читателям."
                          : "Текст будет очищен без возможности восстановления. Ответы сохранятся."}
                </p>
                <blockquote className="modal-quote">
                    {selected?.content}
                </blockquote>
                {error && <Alert tone="error">{error}</Alert>}
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => setSelected(null)}
                    >
                        Отмена
                    </Button>
                    <Button
                        variant={action === "delete" ? "danger" : "primary"}
                        busy={busy}
                        onClick={perform}
                    >
                        {action === "hide"
                            ? "Скрыть"
                            : action === "restore"
                              ? "Восстановить"
                              : "Удалить"}
                    </Button>
                </div>
            </Modal>
        </>
    );
}

export function Taxonomy() {
    const store = useMock();
    const [kind, setKind] = useState<"categories" | "tags">("categories");
    const [dialog, setDialog] = useState<{
        id?: string;
        name: string;
        slug: string;
        version?: string;
    } | null>(null);
    const [deleting, setDeleting] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const items = store[kind];
    const singular = kind === "categories" ? "категорию" : "тег";
    const save = async () => {
        if (!dialog) return;
        setBusy(true);
        setError("");
        try {
            await new Promise((r) => setTimeout(r, 380));
            store.assertAvailable();
            if (!dialog.name.trim() || [...dialog.name.trim()].length > 80)
                throw new Error("Имя должно содержать от 1 до 80 символов.");
            if (!dialog.id && !validSlug(dialog.slug))
                throw new Error(
                    "Slug: латиница, цифры и одиночные дефисы, до 120 символов.",
                );
            if (items.some((x) => x.id !== dialog.id && x.slug === dialog.slug))
                throw new Error("Этот slug уже занят. Выберите другой.");
            if (store.scenario === "conflict" && dialog.id)
                throw new Error(
                    "Запись изменилась. Закройте форму и откройте актуальную версию.",
                );
            const next = {
                id: dialog.id || crypto.randomUUID(),
                name: dialog.name.trim(),
                slug: dialog.slug,
                version: crypto.randomUUID(),
                description: "",
            };
            if (kind === "categories")
                store.setCategories((xs) =>
                    dialog.id
                        ? xs.map((x) =>
                              x.id === dialog.id ? { ...x, ...next } : x,
                          )
                        : [...xs, next],
                );
            else
                store.setTags((xs) =>
                    dialog.id
                        ? xs.map((x) => (x.id === dialog.id ? next : x))
                        : [...xs, next],
                );
            setDialog(null);
            store.toast("Справочник обновлён");
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    };
    const remove = async () => {
        setBusy(true);
        setError("");
        try {
            await new Promise((r) => setTimeout(r, 350));
            store.assertAvailable();
            const field = kind === "categories" ? "categoryIds" : "tagIds";
            if (
                store.posts.some(
                    (p) => !p.deleted && p[field].includes(deleting!),
                )
            )
                throw new Error(
                    "Запись используется в материалах, включая черновики. Сначала снимите связи в редакторе публикаций.",
                );
            if (store.scenario === "conflict")
                throw new Error("Версия записи устарела. Обновите справочник.");
            if (kind === "categories")
                store.setCategories((xs) =>
                    xs.filter((x) => x.id !== deleting),
                );
            else store.setTags((xs) => xs.filter((x) => x.id !== deleting));
            setDeleting(null);
            store.toast("Запись удалена");
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
                    <span className="eyebrow">ПО ПОЛОЧКАМ</span>
                    <h1>
                        Категории и теги<span className="heading-dot">.</span>
                    </h1>
                    <p>Помогают читателям находить близкие темы.</p>
                </div>
                <Button
                    onClick={() => {
                        setError("");
                        setDialog({ name: "", slug: "" });
                    }}
                >
                    Добавить {singular}
                </Button>
            </div>
            <div className="tabs" role="tablist">
                {[
                    ["categories", "Категории"],
                    ["tags", "Теги"],
                ].map(([key, label]) => (
                    <button
                        key={key}
                        role="tab"
                        aria-selected={kind === key}
                        className={kind === key ? "active" : ""}
                        onClick={() => setKind(key as "categories" | "tags")}
                    >
                        {label}
                        <span>
                            {store[key as "categories" | "tags"].length}
                        </span>
                    </button>
                ))}
            </div>
            <ScenarioState>
                <div className="taxonomy-table">
                    <div className="taxonomy-table-head">
                        <span>НАЗВАНИЕ</span>
                        <span>SLUG</span>
                        <span>ДЕЙСТВИЯ</span>
                    </div>
                    {[...items]
                        .sort((a, b) => a.name.localeCompare(b.name, "en"))
                        .map((item) => (
                            <div className="taxonomy-row" key={item.id}>
                                <strong>
                                    {kind === "tags" ? "#" : ""}
                                    {item.name}
                                </strong>
                                <code>{item.slug}</code>
                                <div>
                                    <button
                                        className="icon-button"
                                        aria-label={`Переименовать ${item.name}`}
                                        onClick={() => {
                                            setError("");
                                            setDialog(item);
                                        }}
                                    >
                                        <Pencil size={16} />
                                    </button>
                                    <button
                                        className="icon-button"
                                        aria-label={`Удалить ${item.name}`}
                                        onClick={() => {
                                            setError("");
                                            setDeleting(item.id);
                                        }}
                                    >
                                        <Trash2 size={16} />
                                    </button>
                                </div>
                            </div>
                        ))}
                </div>
            </ScenarioState>
            <p className="tiny muted taxonomy-note">
                Slug задаётся при создании и остаётся постоянным. Удалить
                используемую запись нельзя.
            </p>
            <Modal
                open={!!dialog}
                onClose={() => setDialog(null)}
                title={`${dialog?.id ? "Переименовать" : "Добавить"} ${singular}`}
            >
                <form
                    onSubmit={(e) => {
                        e.preventDefault();
                        save();
                    }}
                >
                    <Field label="Название" name="taxonomy-name" required>
                        <input
                            id="taxonomy-name"
                            value={dialog?.name || ""}
                            onChange={(e) =>
                                setDialog((d) =>
                                    d ? { ...d, name: e.target.value } : d,
                                )
                            }
                            maxLength={160}
                            autoFocus
                        />
                    </Field>
                    {!dialog?.id && (
                        <Field
                            label="Slug"
                            name="taxonomy-slug"
                            hint="Латиница в нижнем регистре, цифры и одиночные дефисы."
                            required
                        >
                            <input
                                id="taxonomy-slug"
                                value={dialog?.slug || ""}
                                onChange={(e) =>
                                    setDialog((d) =>
                                        d ? { ...d, slug: e.target.value } : d,
                                    )
                                }
                            />
                        </Field>
                    )}
                    {error && <Alert tone="error">{error}</Alert>}
                    <div className="modal-footer">
                        <Button
                            type="button"
                            variant="secondary"
                            onClick={() => setDialog(null)}
                        >
                            Отмена
                        </Button>
                        <Button type="submit" busy={busy}>
                            Сохранить
                        </Button>
                    </div>
                </form>
            </Modal>
            <Modal
                open={!!deleting}
                onClose={() => setDeleting(null)}
                title={`Удалить ${singular}?`}
            >
                <p>
                    Запись исчезнет из справочника. Если она используется хотя
                    бы в одном материале, удаление будет отклонено.
                </p>
                {error && <Alert tone="error">{error}</Alert>}
                <div className="modal-footer">
                    <Button
                        variant="secondary"
                        onClick={() => setDeleting(null)}
                    >
                        Отмена
                    </Button>
                    <Button variant="danger" busy={busy} onClick={remove}>
                        Удалить
                    </Button>
                </div>
            </Modal>
        </>
    );
}
