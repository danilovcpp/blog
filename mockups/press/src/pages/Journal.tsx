import { useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import {
    ArrowDown,
    ArrowRight,
    ArrowUpRight,
    Check,
    ChevronDown,
    Eye,
    Grid2X2,
    Heart,
    Layers,
    List,
    SlidersHorizontal,
    Sparkles,
    X,
} from "lucide-react";
import { useMock } from "../mock/store";
import { formatDate, localId, readTime, type Post } from "../mock/data";
import { theme } from "../themes";
import { matchesFeedFilters } from "../mock/rules";
import {
    Badge,
    Button,
    EmptyState,
    ScenarioState,
    Alert,
} from "../components/ui";

export function PostCard({
    post,
    compact = false,
}: {
    post: Post;
    compact?: boolean;
}) {
    const { categories } = useMock();
    const cat = categories.find((c) => post.categoryIds.includes(c.id));
    return (
        <article className={`post-card ${compact ? "post-card-list" : ""}`}>
            <Link
                className="card-image-link"
                to={`/posts/${post.slug}`}
                tabIndex={-1}
                aria-hidden="true"
            >
                <img
                    className="card-image"
                    src={post.coverUrl}
                    alt=""
                    loading="lazy"
                />
                <span className="image-arrow">
                    <ArrowUpRight size={20} />
                </span>
            </Link>
            <div className="card-body">
                <div className="card-kicker">
                    <Link to={`/category/${cat?.slug}`}>{cat?.name}</Link>
                    <span>{readTime(post.markdown)} мин чтения</span>
                </div>
                <h3>
                    <Link to={`/posts/${post.slug}`}>{post.title}</Link>
                </h3>
                <p>{post.description}</p>
                <div className="card-meta">
                    <span>{formatDate(post.firstPublishedAt!, true)}</span>
                    <span className="meta-counters">
                        <span>
                            <Heart size={13} />
                            {post.likeCount}
                        </span>
                        <span>
                            <Eye size={14} />
                            {new Intl.NumberFormat("ru", {
                                notation: "compact",
                            }).format(post.viewCount)}
                        </span>
                    </span>
                </div>
            </div>
        </article>
    );
}
export function Journal() {
    const store = useMock();
    const { categorySlug, tagSlug } = useParams();
    const [params, setParams] = useSearchParams();
    const [filtersOpen, setFiltersOpen] = useState(false);
    const [limit, setLimit] = useState(6);
    const [list, setList] = useState(false);
    const [pageError, setPageError] = useState(false);
    const category = store.categories.find((c) => c.slug === categorySlug);
    const tag = store.tags.find((t) => t.slug === tagSlug);
    const categoryIds = category ? [category.id] : params.getAll("categoryIds");
    const tagIds = tag ? [tag.id] : params.getAll("tagIds");
    const sort = params.get("sort") || "recent";
    const hasFilters = !!(categoryIds.length || tagIds.length);
    const published = store.posts.filter(
        (p) => p.state === "Published" && !p.deleted,
    );
    const posts = published
        .filter((p) => matchesFeedFilters(p, categoryIds, tagIds))
        .sort((a, b) =>
            sort === "popular"
                ? b.likeCount - a.likeCount ||
                  b.firstPublishedAt!.localeCompare(a.firstPublishedAt!)
                : b.firstPublishedAt!.localeCompare(a.firstPublishedAt!),
        );
    const hero = !hasFilters && sort === "recent" ? posts[0] : null;
    const cards = hero ? posts.slice(1) : posts;
    const setParam = (key: string, value: string) => {
        const next = new URLSearchParams(params);
        next.set(key, value);
        setParams(next);
        setLimit(6);
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
        setLimit(6);
    };
    if ((categorySlug && !category) || (tagSlug && !tag)) return <NotFound />;
    return (
        <main className="journal container">
            {hero ? (
                <section className="hero">
                    <div className="hero-copy">
                        <div className="eyebrow">
                            <span className="small-star">✳</span> ПРОСТРАНСТВО
                            ДЛЯ ЛЮБОПЫТСТВА
                        </div>
                        <h1>
                            Замечать больше.
                            <br />
                            <em>Думать глубже.</em>
                        </h1>
                        <p>
                            Идеи, люди и вещи, которые делают
                            <br className="desktop-only" /> повседневность
                            немного интереснее.
                        </p>
                        <a href="#journal" className="text-link">
                            Найти что-то своё <ArrowDown size={17} />
                        </a>
                        <div className="hero-bottom">
                            <span className="hero-orbit">✳</span>
                            <span>
                                Новые истории.
                                <br />
                                Без лишнего шума.
                            </span>
                            <span className="hero-edition">ОКТЯБРЬ / 2026</span>
                        </div>
                    </div>
                    <Link className="hero-feature" to={`/posts/${hero.slug}`}>
                        <img
                            src={hero.coverUrl || theme.hero}
                            alt={
                                (hero.coverUrl || theme.hero) === theme.hero
                                    ? theme.heroAlt
                                    : ""
                            }
                        />
                        <div className="hero-image-shade" />
                        <span className="hero-feature-label">
                            {store.categories
                                .find((c) => hero.categoryIds.includes(c.id))
                                ?.name.toUpperCase() || "НОВАЯ ИСТОРИЯ"}
                        </span>
                        <span className="hero-feature-content">
                            <span className="hero-feature-title">
                                {hero.title}
                            </span>
                            <span className="hero-feature-meta">
                                {readTime(hero.markdown)} мин наедине с хорошим
                                текстом
                            </span>
                        </span>
                        <span className="hero-feature-arrow">
                            <ArrowUpRight size={26} />
                        </span>
                    </Link>
                </section>
            ) : (
                <section className="collection-heading">
                    <Link className="breadcrumb" to="/">
                        Журнал <ArrowRight size={13} />
                    </Link>
                    <span className="eyebrow">
                        {tag ? "ИСТОРИИ ПО ТЕГУ" : "ТЕМАТИЧЕСКАЯ КОЛЛЕКЦИЯ"}
                    </span>
                    <h1>
                        {category?.name ||
                            (tag ? `#${tag.name}` : "Выбранные истории")}
                    </h1>
                    <p>
                        {category?.description ||
                            "Несколько точек зрения на то, что вам интересно."}
                    </p>
                </section>
            )}
            <section id="journal" className="feed-section">
                <div className="topic-strip">
                    <Link
                        className={`topic-chip ${!categoryIds.length ? "active" : ""}`}
                        to={tag ? `/tag/${tag.slug}` : "/"}
                    >
                        <Layers size={15} />
                        Все темы
                    </Link>
                    {store.categories.map((c) => (
                        <Link
                            key={c.id}
                            to={`/category/${c.slug}`}
                            className={`topic-chip ${categoryIds.includes(c.id) ? "active" : ""}`}
                        >
                            {c.name}
                        </Link>
                    ))}
                </div>
                <div className="feed-title-row">
                    <div className="section-title">
                        <span className="section-overline">
                            ЕСТЬ О ЧЁМ ПОДУМАТЬ
                        </span>
                        <h2>
                            {hasFilters ? "В этой коллекции" : "Свежий взгляд"}
                            <span className="heading-dot">.</span>
                        </h2>
                    </div>
                    <div className="feed-controls">
                        <div className="segmented" aria-label="Сортировка">
                            <button
                                className={sort === "recent" ? "active" : ""}
                                onClick={() => setParam("sort", "recent")}
                            >
                                Новое
                            </button>
                            <button
                                className={sort === "popular" ? "active" : ""}
                                onClick={() => setParam("sort", "popular")}
                            >
                                Популярное
                            </button>
                        </div>
                        <button
                            className={`icon-button filter-toggle ${filtersOpen ? "selected" : ""}`}
                            aria-label="Показать фильтры"
                            aria-expanded={filtersOpen}
                            onClick={() => setFiltersOpen(!filtersOpen)}
                        >
                            <SlidersHorizontal size={18} />
                            {hasFilters && (
                                <span className="filter-count">
                                    {categoryIds.length + tagIds.length}
                                </span>
                            )}
                        </button>
                        <button
                            className="icon-button view-toggle"
                            aria-label={
                                list ? "Показать сеткой" : "Показать списком"
                            }
                            onClick={() => setList(!list)}
                        >
                            {list ? <Grid2X2 size={18} /> : <List size={19} />}
                        </button>
                    </div>
                </div>
                {filtersOpen && (
                    <div className="filter-panel">
                        <div>
                            <strong>Категории</strong>
                            <span className="field-hint">
                                Любая из выбранных
                            </span>
                            <div className="filter-chips">
                                {store.categories.map((c) => (
                                    <button
                                        key={c.id}
                                        className={`filter-chip ${categoryIds.includes(c.id) ? "active" : ""}`}
                                        disabled={!!category}
                                        onClick={() =>
                                            toggleFilter("categoryIds", c.id)
                                        }
                                    >
                                        {categoryIds.includes(c.id) && (
                                            <Check size={13} />
                                        )}{" "}
                                        {c.name}
                                    </button>
                                ))}
                            </div>
                        </div>
                        <div>
                            <strong>Теги</strong>
                            <span className="field-hint">
                                Все выбранные одновременно
                            </span>
                            <div className="filter-chips">
                                {store.tags.map((t) => (
                                    <button
                                        key={t.id}
                                        className={`filter-chip ${tagIds.includes(t.id) ? "active" : ""}`}
                                        disabled={!!tag}
                                        onClick={() =>
                                            toggleFilter("tagIds", t.id)
                                        }
                                    >
                                        {tagIds.includes(t.id) && (
                                            <Check size={13} />
                                        )}
                                        #{t.name}
                                    </button>
                                ))}
                            </div>
                        </div>
                        {(params.has("categoryIds") ||
                            params.has("tagIds")) && (
                            <Button
                                variant="ghost"
                                onClick={() => {
                                    setParams(
                                        sort === "popular" ? { sort } : {},
                                    );
                                    setLimit(6);
                                }}
                            >
                                <X size={14} />
                                Сбросить фильтры
                            </Button>
                        )}
                    </div>
                )}
                <ScenarioState>
                    {cards.length ? (
                        <div className={list ? "posts-list" : "posts-grid"}>
                            {cards.slice(0, limit).map((p) => (
                                <PostCard key={p.id} post={p} compact={list} />
                            ))}
                        </div>
                    ) : (
                        <EmptyState
                            title="Такого сочетания пока нет"
                            text="Попробуйте убрать один из тегов или выбрать другую тему."
                        >
                            <Link className="button button-secondary" to="/">
                                Все материалы
                            </Link>
                        </EmptyState>
                    )}
                    {pageError && (
                        <Alert tone="error">
                            Не удалось загрузить следующую страницу. Уже
                            загруженные материалы доступны.{" "}
                            <button
                                className="inline-button"
                                onClick={() => {
                                    setPageError(false);
                                    store.setScenario("normal");
                                    setLimit((l) => l + 6);
                                }}
                            >
                                Повторить
                            </button>
                        </Alert>
                    )}
                    {limit < cards.length && !pageError && (
                        <div className="load-more">
                            <Button
                                variant="secondary"
                                onClick={() =>
                                    store.scenario === "page-error"
                                        ? setPageError(true)
                                        : setLimit((l) => l + 6)
                                }
                            >
                                Ещё немного интересного <ArrowDown size={16} />
                            </Button>
                            <span>
                                Без спешки. Хорошие тексты никуда не уйдут.
                            </span>
                        </div>
                    )}
                </ScenarioState>
            </section>
            {!store.authed && (
                <section className="join-banner">
                    <span className="join-symbol">✳</span>
                    <div>
                        <span className="eyebrow">ХОРОШО, ЧТО ВЫ ЗДЕСЬ</span>
                        <h2>
                            У хороших мыслей
                            <br />
                            должна быть компания.
                        </h2>
                        <p>
                            Присоединяйтесь, чтобы делиться откликами
                            <br />и поддерживать то, что вам близко.
                        </p>
                    </div>
                    <Link className="button button-primary" to="/auth/register">
                        Стать частью Слоя <ArrowUpRight size={18} />
                    </Link>
                </section>
            )}
        </main>
    );
}

export function Topics() {
    const { categories, tags } = useMock();
    return (
        <main className="container topics-page">
            <section className="page-heading">
                <span className="eyebrow">СЛЕДУЙТЕ ЗА ЛЮБОПЫТСТВОМ</span>
                <h1>
                    У каждого интереса
                    <br />
                    <em>есть свой слой.</em>
                </h1>
                <p>Выберите направление. А дальше — как пойдёт.</p>
            </section>
            <ScenarioState>
                <div className="topic-grid">
                    {categories.map((c, i) => (
                        <Link
                            className="topic-tile"
                            key={c.id}
                            to={`/category/${c.slug}`}
                        >
                            <img src={theme.covers[i % 6]} alt="" />
                            <span className="topic-tile-shade" />
                            <span className="topic-index">0{i + 1}</span>
                            <div>
                                <h2>{c.name}</h2>
                                <p>{c.description}</p>
                            </div>
                            <ArrowUpRight size={25} />
                        </Link>
                    ))}
                </div>
                <section className="tags-section">
                    <span className="eyebrow">ПО ОДНОМУ СЛОВУ</span>
                    <h2>Или начните с идеи</h2>
                    <div className="large-tags">
                        {tags.map((t) => (
                            <Link key={t.id} to={`/tag/${t.slug}`}>
                                #{t.name} <ArrowUpRight size={15} />
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
        <main className="container about-page">
            <span className="eyebrow">НЕЗАВИСИМЫЙ ЖУРНАЛ</span>
            <h1>
                У мира много слоёв.
                <br />
                <em>Давайте замечать их.</em>
            </h1>
            <p className="about-intro">
                Слой — место, где можно ненадолго выйти из потока и прочитать
                что-то не потому, что «надо», а потому, что интересно.
            </p>
            <img
                className="about-image"
                src="/images/mountains.jpg"
                alt="Тихое горное озеро в обрамлении леса"
            />
            <div className="about-columns">
                <h2>
                    Меньше шума.
                    <br />
                    Больше смысла.
                </h2>
                <div>
                    <p>
                        Мы пишем о среде, технологиях, культуре и повседневной
                        жизни. Нас объединяет не одна тема, а способ смотреть: с
                        любопытством, вниманием и уважением к деталям.
                    </p>
                    <p>
                        Здесь нет гонки за новостями. Есть истории, к которым
                        хочется возвращаться, и разговоры, в которых важно
                        слышать друг друга.
                    </p>
                    <Link className="text-link" to="/">
                        Открыть журнал <ArrowRight size={17} />
                    </Link>
                </div>
            </div>
        </main>
    );
}

export function NotFound({
    code = "404",
    title = "Кажется, этот слой ещё не открыт",
    text = "Материал мог быть снят с публикации, удалён или адрес оказался неточным.",
}: {
    code?: string;
    title?: string;
    text?: string;
}) {
    return (
        <main className="container not-found">
            <span className="error-number">{code}</span>
            <h1>{title}</h1>
            <p>{text}</p>
            <Link className="button button-primary" to="/">
                Вернуться в журнал <ArrowRight size={17} />
            </Link>
        </main>
    );
}
