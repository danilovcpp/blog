import { brand } from "../brands";
import { useState } from "react";
import {
    Link,
    useLocation,
    useNavigate,
    useParams,
    useSearchParams,
} from "react-router-dom";
import {
    ArrowDown,
    ArrowRight,
    ArrowUpRight,
    Check,
    ChevronRight,
    Clock3,
    Cpu,
    Flame,
    Radio,
    Search,
    SlidersHorizontal,
    Tag,
    Wrench,
    X,
} from "lucide-react";
import { useMock } from "../mock/store";
import {
    formatDate,
    localId,
    readTime,
    shortAuthor,
    type Post,
} from "../mock/data";
import { theme } from "../themes";
import { tagTone } from "../themes/tones";
import { matchesFeedFilters, commentInPublicTree } from "../mock/rules";
import { Button, EmptyState, ScenarioState, Alert } from "../components/ui";
import { PostPreview } from "../components/PostPreview";
import { PostIcon } from "../components/PostIcon";
import { TypewriterText } from "../components/TypewriterText";

const number = (value: number) =>
    new Intl.NumberFormat("ru", { notation: "compact" }).format(value);
export function PostCard({
    post,
    compact = false,
}: {
    post: Post;
    compact?: boolean;
}) {
    const store = useMock();
    const navigate = useNavigate();
    const cat = store.categories.find((c) => post.categoryIds.includes(c.id));
    const saved = store.bookmarks.includes(post.id);
    const [likeBusy, setLikeBusy] = useState(false);
    const comments = store.comments.filter(
        (c) => c.publicationId === post.id && c.state === "Visible",
    ).length;
    const liked = store.authed && post.likedBy.includes(localId);
    return (
        <article className={`tech-post ${compact ? "compact-post" : ""}`}>
            <div className="post-byline">
                <Link to={`/authors/${post.author.id}`} className="person">
                    <span
                        className={`person-avatar ${post.author.id === localId ? "" : "avatar-ochre"}`}
                    >
                        {post.author.id === localId ? "АП" : "ИК"}
                    </span>
                    <strong>{shortAuthor(post.author.id)}</strong>
                </Link>
                <span className="byline-dot">·</span>
                <time>{formatDate(post.firstPublishedAt!, true)}</time>
                <button
                    className={`save-post ${saved ? "is-saved" : ""}`}
                    aria-label={saved ? "Убрать из закладок" : "В закладки"}
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
                </button>
            </div>
            <h2>
                <Link to={`/posts/${post.slug}`}>{post.title}</Link>
            </h2>
            <div className="post-topics">
                <Link to={`/category/${cat?.slug}`} className="category-label">
                    {cat?.name}
                </Link>
                <span>
                    <Clock3 size={13} />
                    {readTime(post.markdown)} мин чтения
                </span>
                {post.tagIds.slice(0, 2).map((id) => {
                    const tag = store.tags.find((t) => t.id === id);
                    return tag ? (
                        <Link
                            className="post-tag"
                            data-tone={tagTone(tag.slug)}
                            key={id}
                            to={`/tag/${tag.slug}`}
                        >
                            #{tag.name}
                        </Link>
                    ) : null;
                })}
            </div>
            {post.coverUrl && (
                <Link
                    className="tech-post-image"
                    to={`/posts/${post.slug}`}
                    tabIndex={-1}
                    aria-hidden="true"
                >
                    <img src={post.coverUrl} alt="" loading="lazy" />
                    <span className="photo-label">
                        {brand.article.photoLabel}
                    </span>
                </Link>
            )}
            {compact ? (
                <p className="post-description">{post.description}</p>
            ) : (
                <PostPreview
                    description={post.description}
                    markdown={post.markdown}
                />
            )}
            <div className="post-bottom">
                <Link className="read-more" to={`/posts/${post.slug}`}>
                    Читать далее <ArrowRight size={15} />
                </Link>
                <div className="post-counters">
                    <span
                        title="Просмотры"
                        aria-label={`Просмотры: ${post.viewCount}`}
                    >
                        <PostIcon name="views" />
                        {number(post.viewCount)}
                    </span>
                    <Link
                        to={`/posts/${post.slug}#comments`}
                        aria-label={`Комментарии: ${comments}`}
                    >
                        <PostIcon name="comments" />
                        {comments}
                    </Link>
                    <button
                        aria-label={liked ? "Убрать лайк" : "Нравится"}
                        aria-pressed={liked}
                        aria-busy={likeBusy}
                        disabled={likeBusy}
                        className={`post-like${liked ? " is-active" : ""}`}
                        onClick={async () => {
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
                        }}
                    >
                        <PostIcon name="heart" active={liked} />
                        {post.likeCount}
                    </button>
                </div>
            </div>
        </article>
    );
}

export function CommunityAside() {
    const store = useMock();
    return (
        <aside className="community-aside">
            <section className="community-note">
                <div className="note-top">
                    <Cpu size={23} />
                    <span>{brand.feed.asideEyebrow}</span>
                </div>
                <h2>
                    {brand.feed.asideTitle[0]}
                    <br />
                    {brand.feed.asideTitle[1]}
                    <br />
                    <em>{brand.feed.asideTitle[2]}</em>
                </h2>
                <p>{brand.feed.asideText}</p>
                <Link
                    to={
                        store.authed
                            ? store.hasRole("Author")
                                ? "/workspace/new"
                                : "/account/profile"
                            : "/auth/register"
                    }
                >
                    Присоединиться <ArrowUpRight size={17} />
                </Link>
                <div className="note-circuit" aria-hidden="true">
                    ⌁
                </div>
            </section>
            <section className="side-panel">
                <div className="panel-title">
                    <h3>
                        <Flame size={17} />
                        Сейчас читают
                    </h3>
                    <span className="live-dot" />
                </div>
                {[...store.posts]
                    .filter((p) => p.state === "Published" && !p.deleted)
                    .sort((a, b) => b.likeCount - a.likeCount)
                    .slice(0, 3)
                    .map((post, i) => (
                        <Link
                            className="trending-item"
                            key={post.id}
                            to={`/posts/${post.slug}`}
                        >
                            <span className="trending-index">0{i + 1}</span>
                            <div>
                                <h4>{post.title}</h4>
                                <span>
                                    <PostIcon name="views" />
                                    {number(post.viewCount)}
                                    <PostIcon name="heart" />
                                    {post.likeCount}
                                </span>
                            </div>
                        </Link>
                    ))}
                <Link className="panel-link" to="/?sort=popular">
                    Всё популярное <ArrowRight size={14} />
                </Link>
            </section>
            <section className="side-panel">
                <div className="panel-title">
                    <h3>
                        <Tag size={17} />
                        {brand.feed.tagsTitle}
                    </h3>
                    <Link to="/topics" aria-label="Все темы">
                        <ArrowUpRight size={16} />
                    </Link>
                </div>
                <div className="side-tags">
                    {store.tags.slice(0, 10).map((t) => (
                        <Link
                            key={t.id}
                            to={`/tag/${t.slug}`}
                            data-tone={tagTone(t.slug)}
                        >
                            #{t.name}
                        </Link>
                    ))}
                </div>
            </section>
            <section className="workshop-tip">
                <span className="tip-label">ЗАМЕТКА НА ПОЛЯХ</span>
                <Wrench size={22} />
                <h3>
                    {brand.feed.tipTitle[0]}
                    <br />
                    {brand.feed.tipTitle[1]}
                </h3>
                <p>{brand.feed.tipText}</p>
                <Link to={brand.feed.tipHref}>
                    {brand.feed.tipLabel} <ArrowRight size={14} />
                </Link>
            </section>
            <div className="aside-footnote">
                <Cpu size={15} />
                <span>
                    {brand.footer.motto[0]}
                    <br />
                    {brand.footer.motto[1]}
                </span>
            </div>
        </aside>
    );
}

export function Journal() {
    const store = useMock();
    const location = useLocation();
    const { categorySlug, tagSlug, authorId } = useParams();
    const [params, setParams] = useSearchParams();
    const [filtersOpen, setFiltersOpen] = useState(false);
    const [limit, setLimit] = useState(4);
    const [pageError, setPageError] = useState(false);
    const category = store.categories.find((c) => c.slug === categorySlug);
    const tag = store.tags.find((t) => t.slug === tagSlug);
    const categoryIds = category ? [category.id] : params.getAll("categoryIds");
    const tagIds = tag ? [tag.id] : params.getAll("tagIds");
    const sort = params.get("sort") || "recent";
    const query = params.get("q") || "";
    const bookmarks = location.pathname === "/bookmarks";
    const searching = location.pathname === "/search";
    const home =
        !categorySlug && !tagSlug && !authorId && !bookmarks && !searching;
    const activeFilters = categoryIds.length + tagIds.length;
    const feedLabel = bookmarks
        ? "Сохранённое"
        : authorId
          ? "Публикации автора"
          : searching
            ? "Результаты поиска"
            : tag
              ? `Материалы #${tag.name}`
              : category?.name || brand.feed.title;
    const posts = store.posts
        .filter(
            (p) =>
                p.state === "Published" &&
                !p.deleted &&
                matchesFeedFilters(p, categoryIds, tagIds) &&
                (!authorId || p.author.id === authorId) &&
                (!bookmarks || store.bookmarks.includes(p.id)) &&
                (!query ||
                    `${p.title} ${p.description}`
                        .toLowerCase()
                        .includes(query.toLowerCase())),
        )
        .sort(
            (a, b) =>
                (sort === "popular" ? b.likeCount - a.likeCount : 0) ||
                b.firstPublishedAt!.localeCompare(a.firstPublishedAt!) ||
                b.id.localeCompare(a.id),
        );
    const changeParam = (key: string, value: string) => {
        const next = new URLSearchParams(params);
        next.set(key, value);
        setParams(next);
        setLimit(4);
    };
    const toggleFilter = (key: string, value: string) => {
        const next = new URLSearchParams(params);
        const values = next.getAll(key);
        next.delete(key);
        for (const id of values.includes(value)
            ? values.filter((x) => x !== value)
            : [...values, value])
            next.append(key, id);
        setParams(next);
        setLimit(4);
    };
    if (
        (categorySlug && !category) ||
        (tagSlug && !tag) ||
        (authorId &&
            ![localId, "10000000-0000-4000-8000-000000000002"].includes(
                authorId,
            ))
    )
        return <NotFound />;
    return (
        <main className="tech-home container">
            {home && (
                <section className="workshop-hero">
                    <img src={theme.hero} alt={theme.heroAlt} />
                    <div className="workshop-hero-shade" />
                    <div className="workshop-hero-copy">
                        <span className="hero-eyebrow">
                            <span />
                            {brand.hero.eyebrow}
                        </span>
                        <h1>
                            {brand.hero.title[0]}
                            <br /> <em>{brand.hero.title[1]}</em>
                        </h1>
                        <p>{brand.hero.description}</p>
                        <a className="button button-primary" href="#feed">
                            {brand.hero.button} <ArrowRight size={17} />
                        </a>
                        <span className="hero-caption">
                            <Radio size={14} /> {brand.hero.caption}
                        </span>
                    </div>
                    <span className="hero-image-credit">
                        {brand.hero.credit}
                    </span>
                </section>
            )}
            {!home && (
                <div className="collection-heading tech-collection">
                    <Link className="breadcrumb" to="/">
                        Главная <ChevronRight size={12} />
                    </Link>
                    <span className="eyebrow">
                        {authorId
                            ? "АВТОР СООБЩЕСТВА"
                            : bookmarks
                              ? "ВАША ПОДБОРКА"
                              : searching
                                ? "ПОИСК В СООБЩЕСТВЕ"
                                : tag
                                  ? "ТЕГ"
                                  : "ТЕМАТИЧЕСКАЯ ЛЕНТА"}
                    </span>
                    <h1>
                        {authorId
                            ? shortAuthor(authorId)
                            : bookmarks
                              ? "Закладки"
                              : searching
                                ? "Найдём что-нибудь полезное"
                                : category?.name || `#${tag?.name}`}
                    </h1>
                    <p>
                        {authorId
                            ? brand.authorBio
                            : bookmarks
                              ? "Материалы, к которым хочется вернуться. Сохранены в этой вкладке."
                              : searching
                                ? "Поиск по заголовкам и описаниям тестовых материалов."
                                : category?.description ||
                                  "Опыт, заметки и находки по одной теме."}
                    </p>
                    {searching && (
                        <form
                            className="search-page-form"
                            onSubmit={(e) => {
                                e.preventDefault();
                                setLimit(4);
                            }}
                        >
                            <Search size={19} />
                            <input
                                aria-label="Поиск по материалам"
                                value={query}
                                onChange={(e) =>
                                    changeParam("q", e.target.value)
                                }
                                placeholder={brand.searchPlaceholder}
                            />
                            <Button type="submit">Найти</Button>
                        </form>
                    )}
                </div>
            )}
            {home && (
                <div className="community-strip">
                    <span>
                        <Radio size={18} />
                        <strong>{brand.feed.strip[0]}</strong>
                        {brand.feed.stripPhrases.length ? (
                            <TypewriterText phrases={brand.feed.stripPhrases} />
                        ) : (
                            <i>{brand.feed.strip[1]}</i>
                        )}
                    </span>
                    <div>
                        <span className="avatar-stack">
                            <b>АП</b>
                            <b>ИК</b>
                            <b>СН</b>
                        </span>
                        <span>
                            {brand.feed.strip[2]}
                            <br />
                            <strong>{brand.feed.strip[3]}</strong>
                        </span>
                    </div>
                    <Link to="/about">
                        Познакомиться с проектом <ArrowUpRight size={15} />
                    </Link>
                </div>
            )}
            <div className="tech-feed-layout" id="feed">
                <section className="feed-main" aria-label={feedLabel}>
                    <div className="feed-toolbar">
                        <div
                            className="feed-tabs"
                            role="group"
                            aria-label="Сортировка ленты"
                        >
                            <button
                                className={sort === "recent" ? "active" : ""}
                                aria-pressed={sort === "recent"}
                                onClick={() => changeParam("sort", "recent")}
                            >
                                Свежее
                            </button>
                            <button
                                className={sort === "popular" ? "active" : ""}
                                aria-pressed={sort === "popular"}
                                onClick={() => changeParam("sort", "popular")}
                            >
                                <Flame size={14} />
                                Популярное
                            </button>
                        </div>
                        <button
                            className={`filter-button ${filtersOpen ? "active" : ""}`}
                            aria-label="Фильтры ленты"
                            aria-expanded={filtersOpen}
                            aria-controls={
                                filtersOpen ? "feed-filter-panel" : undefined
                            }
                            onClick={() => setFiltersOpen(!filtersOpen)}
                        >
                            <SlidersHorizontal size={16} />
                            <span>
                                Фильтры
                                {activeFilters ? ` · ${activeFilters}` : ""}
                            </span>
                        </button>
                    </div>
                    <nav
                        className="tech-topic-strip"
                        aria-label="Категории публикаций"
                    >
                        <Link
                            className={!categorySlug ? "active" : ""}
                            aria-current={!categorySlug ? "true" : undefined}
                            to={bookmarks ? "/bookmarks" : "/"}
                        >
                            Все
                        </Link>
                        {store.categories.map((c) => (
                            <Link
                                key={c.id}
                                className={
                                    category?.id === c.id ? "active" : ""
                                }
                                aria-current={
                                    category?.id === c.id ? "page" : undefined
                                }
                                to={`/category/${c.slug}`}
                            >
                                {c.name}
                            </Link>
                        ))}
                    </nav>
                    {filtersOpen && (
                        <div className="filter-panel" id="feed-filter-panel">
                            <div>
                                <strong>Категории</strong>
                                <span className="field-hint">
                                    Подходит любая выбранная категория
                                </span>
                                <div className="filter-chips">
                                    {store.categories.map((c) => (
                                        <button
                                            className={`filter-chip ${categoryIds.includes(c.id) ? "active" : ""}`}
                                            aria-pressed={categoryIds.includes(
                                                c.id,
                                            )}
                                            key={c.id}
                                            disabled={!!category}
                                            onClick={() =>
                                                toggleFilter(
                                                    "categoryIds",
                                                    c.id,
                                                )
                                            }
                                        >
                                            {categoryIds.includes(c.id) && (
                                                <Check size={12} />
                                            )}{" "}
                                            {c.name}
                                        </button>
                                    ))}
                                </div>
                            </div>
                            <div>
                                <strong>Теги</strong>
                                <span className="field-hint">
                                    Материал содержит все выбранные теги
                                </span>
                                <div className="filter-chips">
                                    {store.tags.map((t) => (
                                        <button
                                            className={`filter-chip ${tagIds.includes(t.id) ? "active" : ""}`}
                                            data-tone={tagTone(t.slug)}
                                            aria-pressed={tagIds.includes(t.id)}
                                            key={t.id}
                                            disabled={!!tag}
                                            onClick={() =>
                                                toggleFilter("tagIds", t.id)
                                            }
                                        >
                                            {tagIds.includes(t.id) && (
                                                <Check size={12} />
                                            )}{" "}
                                            #{t.name}
                                        </button>
                                    ))}
                                </div>
                            </div>
                            {activeFilters > 0 && (
                                <Button
                                    variant="ghost"
                                    onClick={() =>
                                        setParams(
                                            sort === "popular" ? { sort } : {},
                                        )
                                    }
                                >
                                    <X size={14} />
                                    Сбросить фильтры
                                </Button>
                            )}
                        </div>
                    )}
                    <ScenarioState>
                        {posts.length ? (
                            <div className="tech-posts">
                                {posts.slice(0, limit).map((post) => (
                                    <PostCard key={post.id} post={post} />
                                ))}
                            </div>
                        ) : (
                            <EmptyState
                                title={
                                    bookmarks
                                        ? "Пока ничего не сохранено"
                                        : "Материалы не найдены"
                                }
                                text={
                                    bookmarks
                                        ? "Нажмите на значок закладки у понравившейся статьи."
                                        : "Измените запрос или уберите часть фильтров."
                                }
                            >
                                <Link
                                    className="button button-secondary"
                                    to="/"
                                >
                                    Открыть ленту
                                </Link>
                            </EmptyState>
                        )}
                        {pageError && (
                            <Alert tone="error">
                                Следующая страница не загрузилась. Прочитанные
                                материалы остаются доступны.{" "}
                                <button
                                    className="inline-button"
                                    onClick={() => {
                                        setPageError(false);
                                        store.setScenario("normal");
                                        setLimit((l) => l + 4);
                                    }}
                                >
                                    Попробовать снова
                                </button>
                            </Alert>
                        )}
                        {limit < posts.length && !pageError && (
                            <div className="load-more">
                                <Button
                                    variant="secondary"
                                    onClick={() =>
                                        store.scenario === "page-error"
                                            ? setPageError(true)
                                            : setLimit((l) => l + 4)
                                    }
                                >
                                    {brand.feed.more} <ArrowDown size={16} />
                                </Button>
                                <span>
                                    Показано {Math.min(limit, posts.length)} из{" "}
                                    {posts.length} материалов
                                </span>
                            </div>
                        )}
                    </ScenarioState>
                </section>
                <CommunityAside />
            </div>
        </main>
    );
}

export function Topics() {
    const { categories, tags, posts } = useMock();
    return (
        <main className="container topics-page">
            <div className="page-heading">
                <span className="eyebrow">НАЙДИТЕ СВОЮ ЧАСТОТУ</span>
                <h1>Что сегодня на вашем столе?</h1>
                <p>Один интерес — десятки способов разобраться глубже.</p>
            </div>
            <ScenarioState>
                <div className="tech-category-grid">
                    {categories.map((c, i) => (
                        <Link
                            key={c.id}
                            className="tech-category"
                            to={`/category/${c.slug}`}
                        >
                            <img src={theme.covers[i % 6]} alt="" />
                            <div>
                                <span className="eyebrow">
                                    0{i + 1} / НАПРАВЛЕНИЕ
                                </span>
                                <h2>{c.name}</h2>
                                <p>{c.description}</p>
                                <span>
                                    {
                                        posts.filter(
                                            (p) =>
                                                p.state === "Published" &&
                                                !p.deleted &&
                                                p.categoryIds.includes(c.id),
                                        ).length
                                    }{" "}
                                    материала <ArrowRight size={16} />
                                </span>
                            </div>
                        </Link>
                    ))}
                </div>
                <section className="tags-section">
                    <h2>Всё начинается с детали</h2>
                    <div className="large-tags">
                        {tags.map((t) => (
                            <Link
                                key={t.id}
                                to={`/tag/${t.slug}`}
                                data-tone={tagTone(t.slug)}
                            >
                                #{t.name}
                                <ArrowUpRight size={14} />
                            </Link>
                        ))}
                    </div>
                </section>
            </ScenarioState>
        </main>
    );
}
export function About() {
    return (
        <main className="container tech-about">
            <div className="page-heading">
                <span className="eyebrow">{brand.about.eyebrow}</span>
                <h1>
                    {brand.about.title[0]}
                    <br />
                    <em>{brand.about.title[1]}</em>
                </h1>
                <p>{brand.about.intro}</p>
            </div>
            <img className="about-image" src={theme.hero} alt={theme.heroAlt} />
            <div className="about-columns">
                <h2>
                    {brand.about.sectionTitle[0]}
                    <br />
                    {brand.about.sectionTitle[1]}
                </h2>
                <div>
                    {brand.about.paragraphs.map((p) => (
                        <p key={p}>{p}</p>
                    ))}
                    <Link className="button button-primary" to="/auth/register">
                        Присоединиться <ArrowRight size={16} />
                    </Link>
                </div>
            </div>
            <div className="about-rules">
                <h2>{brand.about.rulesTitle}</h2>
                <p>{brand.about.rules}</p>
            </div>
        </main>
    );
}
export function NotFound({
    code = "404",
    title = "На этой частоте пока тихо",
    text = "Материал мог быть удалён, снят с публикации или адрес оказался неточным.",
}: {
    code?: string;
    title?: string;
    text?: string;
}) {
    return (
        <main className="container not-found">
            <Radio size={34} />
            <span className="error-number">{code}</span>
            <h1>{title}</h1>
            <p>{text}</p>
            <Link className="button button-primary" to="/">
                Вернуться в ленту <ArrowRight size={16} />
            </Link>
        </main>
    );
}
