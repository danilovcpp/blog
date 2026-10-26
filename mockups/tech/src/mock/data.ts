import { getDemoContent } from "../brands/content";
import { brand } from "../brands";
import { theme } from "../themes";
export type Role = "Reader" | "Author" | "Moderator" | "Admin";
export type State = "Draft" | "Published";
export type CommentState = "Visible" | "Hidden" | "Deleted";
export interface Category {
    id: string;
    name: string;
    slug: string;
    version: string;
    description: string;
    count?: number;
}
export interface Tag {
    id: string;
    name: string;
    slug: string;
    version: string;
}
export interface Post {
    id: string;
    title: string;
    slug: string;
    description: string;
    markdown: string;
    coverUrl: string;
    author: { id: string };
    categoryIds: string[];
    tagIds: string[];
    state: State;
    version: string;
    createdAt: string;
    updatedAt: string;
    firstPublishedAt: string | null;
    likeCount: number;
    viewCount: number;
    likedBy: string[];
    deleted?: boolean;
}
export interface Comment {
    id: string;
    publicationId: string;
    parentId: string | null;
    depth: number;
    state: CommentState;
    content: string | null;
    author: { id: string };
    createdAt: string;
    editedAt: string | null;
    version: string;
}
export const localId = "10000000-0000-4000-8000-000000000001";
export const otherId = "10000000-0000-4000-8000-000000000002";
export const uid = () => crypto.randomUUID();
const cid = (n: number) =>
    `20000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const tid = (n: number) =>
    `30000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
export const pid = (n: number) =>
    `40000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const commentId = (n: number) =>
    `50000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
export const initialCategories: Category[] = brand.categories.map(
    ([name, slug, description], i) => ({
        id: cid(i + 1),
        name,
        slug,
        description,
        version: cid(101 + i),
    }),
);
export const initialTags: Tag[] = brand.tags.map(([name, slug], i) => ({
    id: tid(i + 1),
    name,
    slug,
    version: tid(101 + i),
}));
const { seeds, commentSeeds } = getDemoContent(localId, otherId);
export const initialPosts: Post[] = seeds.map((s, i) => ({
    id: pid(i + 1),
    title: s[0],
    slug: s[1],
    description: s[2],
    markdown: s[7],
    coverUrl:
        brand.id === "generic" ? "" : theme.covers[i % theme.covers.length],
    author: { id: i % 3 === 0 ? localId : otherId },
    categoryIds: [cid(s[3])],
    tagIds: s[4].map(tid),
    state: "Published",
    version: cid(300 + i),
    createdAt: `2026-10-${String(26 - i).padStart(2, "0")}T06:00:00Z`,
    updatedAt: `2026-10-${String(26 - i).padStart(2, "0")}T08:00:00Z`,
    firstPublishedAt: `2026-10-${String(26 - i).padStart(2, "0")}T08:00:00Z`,
    likeCount: s[5],
    viewCount: s[6],
    likedBy: i === 1 ? [localId] : [],
}));
initialPosts.push({
    ...initialPosts[0],
    id: pid(10),
    title:
        brand.id === "generic"
            ? "Заметки к следующей публикации"
            : "Восстанавливаем шкалу радиоприёмника",
    slug: brand.id === "generic" ? "next-publication" : "radio-scale",
    description:
        brand.id === "generic"
            ? "Заметки для следующего материала."
            : "Заметки о чистке и сохранении оригинальной шкалы.",
    state: "Draft",
    firstPublishedAt: null,
    markdown:
        brand.id === "generic"
            ? "## Контекст\n\nЗдесь соберём исходные наблюдения и вопросы для следующей публикации."
            : "## Состояние до реставрации\n\nШкала потускнела, но надписи сохранились. Начинаем с фиксации исходного состояния и подбора бережного способа очистки.",
    likeCount: 0,
    viewCount: 0,
    likedBy: [],
    version: cid(310),
});
initialPosts.push({
    ...initialPosts[1],
    id: pid(11),
    title:
        brand.id === "generic"
            ? "Наблюдения после завершения проекта"
            : "Кассетная дека: заметки после ремонта",
    slug: "deck-after-repair",
    author: { id: localId },
    state: "Draft",
    firstPublishedAt: "2026-10-01T08:00:00Z",
    version: cid(311),
});
export const initialComments: Comment[] = commentSeeds.map(
    ([n, parent, depth, state, content, author]) => ({
        id: commentId(n),
        publicationId: pid(1),
        parentId: parent ? commentId(parent) : null,
        depth,
        state,
        content,
        author: { id: author },
        createdAt: `2026-10-26T${String(10 + (n % 10)).padStart(2, "0")}:15:00Z`,
        editedAt: null,
        version: cid(500 + n),
    }),
);
initialComments.push({
    ...initialComments[0],
    id: commentId(7),
    publicationId: pid(2),
    content:
        brand.id === "generic"
            ? "Спасибо, попробую этот подход в своём проекте."
            : "После замены пассика подмотка заработала. Скорость проверю тестовой кассетой.",
    version: cid(507),
});
export const initialSessions = [
    {
        id: "session-1",
        clientId: brand.id,
        userAgent: "Chrome · Windows",
        createdAt: "2026-10-25T10:00:00Z",
        lastUsedAt: "2026-10-27T08:30:00Z",
        absoluteExpiresAt: "2026-11-24T10:00:00Z",
        isCurrent: true,
    },
    {
        id: "session-2",
        clientId: brand.id,
        userAgent: "Safari · iPhone",
        createdAt: "2026-10-20T09:00:00Z",
        lastUsedAt: "2026-10-26T19:20:00Z",
        absoluteExpiresAt: "2026-11-19T09:00:00Z",
        isCurrent: false,
    },
    {
        id: "session-3",
        clientId: brand.id,
        userAgent: "Firefox · macOS",
        createdAt: "2026-10-15T14:00:00Z",
        lastUsedAt: "2026-10-24T15:10:00Z",
        absoluteExpiresAt: "2026-11-14T14:00:00Z",
        isCurrent: false,
    },
];
export const formatDate = (date: string, short = false) =>
    new Intl.DateTimeFormat("ru", {
        day: "numeric",
        month: short ? "short" : "long",
        timeZone: "Asia/Omsk",
    }).format(new Date(date));
export const readTime = (markdown: string) =>
    Math.max(3, Math.ceil(markdown.split(/\s+/).length / 170));
export const shortAuthor = (id: string) =>
    id === localId ? brand.authors[0] : brand.authors[1];
