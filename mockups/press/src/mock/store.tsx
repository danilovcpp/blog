import {
    createContext,
    useContext,
    useState,
    useRef,
    type ReactNode,
} from "react";
import { requireVersion, scalarLength, trimText, viewIsDue } from "./rules";
import {
    initialPosts,
    initialComments,
    initialCategories,
    initialTags,
    initialSessions,
    localId,
    uid,
    type Post,
    type Comment,
    type Category,
    type Tag,
    type Role,
} from "./data";

export type Persona = "guest" | "reader" | "author" | "moderator" | "admin";
export type Scenario =
    | "normal"
    | "loading"
    | "empty"
    | "offline"
    | "unavailable"
    | "conflict"
    | "rate-limit"
    | "invalid"
    | "page-error";
export const personas: Record<Persona, { name: string; roles: Role[] }> = {
    guest: { name: "Гость", roles: [] },
    reader: { name: "Читатель", roles: ["Reader"] },
    author: { name: "Автор", roles: ["Reader", "Author"] },
    moderator: { name: "Модератор", roles: ["Reader", "Moderator"] },
    admin: { name: "Администратор", roles: ["Reader", "Admin"] },
};
export const scenarios: Record<Scenario, string> = {
    normal: "Обычное состояние",
    loading: "Загрузка",
    empty: "Нет данных",
    offline: "Нет подключения",
    unavailable: "Сервис недоступен",
    conflict: "Конфликт версии",
    "rate-limit": "Слишком много запросов",
    invalid: "Неверные данные / ссылка",
    "page-error": "Ошибка следующей страницы",
};
export const delay = (ms = 420) =>
    new Promise<void>((resolve) => setTimeout(resolve, ms));
type Toast = { id: string; text: string; tone?: "success" | "error" };
function useMockState() {
    const [posts, setPosts] = useState<Post[]>(initialPosts);
    const [comments, setComments] = useState<Comment[]>(initialComments);
    const [categories, setCategories] = useState<Category[]>(initialCategories);
    const [tags, setTags] = useState<Tag[]>(initialTags);
    const [sessions, setSessions] = useState(initialSessions);
    const [persona, setPersona] = useState<Persona>("guest");
    const [scenario, setScenario] = useState<Scenario>("normal");
    const [displayName, setDisplayName] = useState("Александр");
    const [toasts, setToasts] = useState<Toast[]>([]);
    const viewRecords = useRef<Record<string, number>>({});
    const visitor = useRef<string>(uid());
    const roles = personas[persona].roles;
    const authed = persona !== "guest";
    const hasRole = (role: Role) =>
        roles.includes(role) || roles.includes("Admin");
    const toast = (text: string, tone: Toast["tone"] = "success") => {
        const id = uid();
        setToasts((items) => [...items, { id, text, tone }]);
        setTimeout(
            () => setToasts((items) => items.filter((t) => t.id !== id)),
            4500,
        );
    };
    const assertAvailable = () => {
        if (scenario === "offline")
            throw new Error(
                "Не удалось подключиться. Проверьте интернет и попробуйте снова.",
            );
        if (scenario === "unavailable")
            throw new Error(
                "Сервис временно недоступен. Ваши изменения остаются в форме.",
            );
    };
    const mutatePost = async (
        post: Post,
        action: "save" | "publish" | "unpublish" | "delete",
    ) => {
        await delay();
        assertAvailable();
        const current = posts.find((p) => p.id === post.id);
        if (current) requireVersion(current.version, post.version);
        if (scenario === "conflict") throw new Error("concurrency_conflict");
        if (
            !hasRole("Author") ||
            (post.author.id !== localId && !hasRole("Admin"))
        )
            throw new Error("Недостаточно прав.");
        if (current?.firstPublishedAt && current.slug !== post.slug)
            throw new Error(
                "После первой публикации адрес материала изменить нельзя.",
            );
        if (posts.some((p) => p.id !== post.id && p.slug === post.slug))
            throw new Error("Этот адрес уже занят. Выберите другой slug.");
        if (
            (action === "publish" || post.state === "Published") &&
            !trimText(post.markdown)
        )
            throw new Error("Перед публикацией добавьте текст.");
        const now = new Date().toISOString();
        const next = {
            ...post,
            state:
                action === "publish"
                    ? "Published"
                    : action === "unpublish"
                      ? "Draft"
                      : post.state,
            deleted: action === "delete",
            firstPublishedAt:
                action === "publish"
                    ? post.firstPublishedAt || now
                    : post.firstPublishedAt,
            version: uid(),
            updatedAt: now,
        } as Post;
        setPosts((items) =>
            current
                ? items.map((p) => (p.id === post.id ? next : p))
                : [next, ...items],
        );
        return next;
    };
    const toggleLike = async (id: string) => {
        await delay(180);
        assertAvailable();
        if (!authed) return;
        setPosts((items) =>
            items.map((p) => {
                if (p.id !== id) return p;
                const liked = p.likedBy.includes(localId);
                return {
                    ...p,
                    likeCount: p.likeCount + (liked ? -1 : 1),
                    likedBy: liked
                        ? p.likedBy.filter((x) => x !== localId)
                        : [...p.likedBy, localId],
                };
            }),
        );
    };
    const registerView = (id: string) => {
        if (
            !posts.some(
                (p) => p.id === id && p.state === "Published" && !p.deleted,
            )
        )
            return;
        // Reserve synchronously: React StrictMode can repeat the opening effect.
        const key = `${id}:${authed ? localId : visitor.current}`;
        const now = Date.now();
        if (!viewIsDue(viewRecords.current[key], now)) return;
        viewRecords.current[key] = now;
        setPosts((items) =>
            items.map((p) =>
                p.id === id ? { ...p, viewCount: p.viewCount + 1 } : p,
            ),
        );
    };
    const saveComment = async (
        publicationId: string,
        content: string,
        parent: Comment | null,
        existing?: Comment,
    ) => {
        await delay();
        assertAvailable();
        if (scenario === "conflict" && existing)
            throw new Error("concurrency_conflict");
        if (!authed) throw new Error("Войдите, чтобы оставить комментарий.");
        const publication = posts.find(
            (p) =>
                p.id === publicationId && p.state === "Published" && !p.deleted,
        );
        if (!publication) throw new Error("Материал недоступен.");
        const normalized = trimText(content);
        if (scalarLength(normalized) < 1 || scalarLength(normalized) > 5000)
            throw new Error("Напишите от 1 до 5000 символов.");
        if (existing) {
            const current = comments.find((c) => c.id === existing.id);
            if (!current) throw new Error("Комментарий недоступен.");
            if (current.author.id !== localId && !hasRole("Admin"))
                throw new Error("Недостаточно прав.");
            if (current.state === "Hidden" && !hasRole("Admin"))
                throw new Error(
                    "Скрытый комментарий может изменить только администратор.",
                );
            if (current.state === "Deleted")
                throw new Error("Удалённый комментарий нельзя изменить.");
            requireVersion(current.version, existing.version);
        }
        if (parent && (parent.depth >= 5 || parent.state !== "Visible"))
            throw new Error("На этот комментарий нельзя ответить.");
        const now = new Date().toISOString();
        const next: Comment = existing
            ? {
                  ...existing,
                  content: normalized,
                  editedAt: now,
                  version: uid(),
              }
            : {
                  id: uid(),
                  publicationId,
                  parentId: parent?.id || null,
                  depth: (parent?.depth || 0) + 1,
                  state: "Visible",
                  content: normalized,
                  author: { id: localId },
                  createdAt: now,
                  editedAt: null,
                  version: uid(),
              };
        setComments((items) =>
            existing
                ? items.map((c) => (c.id === existing.id ? next : c))
                : [...items, next],
        );
        return next;
    };
    const changeComment = async (
        id: string,
        action: "hide" | "restore" | "delete",
    ) => {
        await delay();
        assertAvailable();
        if (scenario === "conflict") throw new Error("concurrency_conflict");
        const current = comments.find((c) => c.id === id);
        if (
            !current ||
            !posts.some(
                (p) =>
                    p.id === current.publicationId &&
                    p.state === "Published" &&
                    !p.deleted,
            )
        )
            throw new Error("Комментарий недоступен.");
        if (
            !authed ||
            (action === "delete"
                ? current.author.id !== localId && !hasRole("Admin")
                : !hasRole("Moderator"))
        )
            throw new Error("Недостаточно прав.");
        if (current.state === "Deleted" && action !== "delete")
            throw new Error("Удалённый комментарий восстановить нельзя.");
        if (
            (current.state === "Hidden" && action === "hide") ||
            (current.state === "Visible" && action === "restore") ||
            (current.state === "Deleted" && action === "delete")
        )
            return;
        setComments((items) =>
            items.map((c) =>
                c.id === id
                    ? {
                          ...c,
                          state:
                              action === "hide"
                                  ? "Hidden"
                                  : action === "restore"
                                    ? "Visible"
                                    : "Deleted",
                          content: action === "delete" ? null : c.content,
                          version: uid(),
                      }
                    : c,
            ),
        );
    };
    const reset = () => {
        viewRecords.current = {};
        setPosts(initialPosts);
        setComments(initialComments);
        setCategories(initialCategories);
        setTags(initialTags);
        setSessions(initialSessions);
        setScenario("normal");
        setPersona("guest");
        setDisplayName("Александр");
        toast("Прототип возвращён к исходному состоянию");
    };
    return {
        posts,
        setPosts,
        comments,
        setComments,
        categories,
        setCategories,
        tags,
        setTags,
        sessions,
        setSessions,
        persona,
        setPersona,
        scenario,
        setScenario,
        displayName,
        setDisplayName,
        roles,
        authed,
        hasRole,
        toast,
        toasts,
        assertAvailable,
        mutatePost,
        toggleLike,
        registerView,
        saveComment,
        changeComment,
        reset,
    };
}
const Context = createContext<ReturnType<typeof useMockState> | null>(null);
export function MockProvider({ children }: { children: ReactNode }) {
    const value = useMockState();
    return <Context.Provider value={value}>{children}</Context.Provider>;
}
export function useMock() {
    const context = useContext(Context);
    if (!context) throw new Error("MockProvider missing");
    return context;
}
