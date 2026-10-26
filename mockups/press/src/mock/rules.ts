/** Pure consumer rules shared by the mock UI and contract boundary tests. */
const whitespace =
    /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g;
export const trimText = (value: string) => value.replace(whitespace, "");
export function scalarLength(value: string) {
    for (const scalar of value) {
        const cp = scalar.codePointAt(0)!;
        if (cp === 0 || (cp >= 0xd800 && cp <= 0xdfff))
            throw new Error("Некорректный символ Unicode.");
    }
    return [...value].length;
}
export function passwordProblem(value: string) {
    const normalized = value.normalize("NFKC");
    const length = [...normalized].length;
    if (length < 15 || length > 128)
        return "От 15 до 128 символов. Можно использовать пробелы и целые фразы.";
    if (
        new TextEncoder().encode(value).length > 1024 ||
        new TextEncoder().encode(normalized).length > 512
    )
        return "Пароль слишком большой в UTF-8. Выберите более короткую фразу.";
    return "";
}
export function matchesFeedFilters(
    post: { categoryIds: string[]; tagIds: string[] },
    categories: string[],
    tags: string[],
) {
    return (
        (!categories.length ||
            categories.some((id) => post.categoryIds.includes(id))) &&
        tags.every((id) => post.tagIds.includes(id))
    );
}
export function commentInPublicTree(
    comment: { id: string; state: string },
    all: { id: string; parentId: string | null; state: string }[],
): boolean {
    return (
        comment.state === "Visible" ||
        all.some(
            (child) =>
                child.parentId === comment.id &&
                commentInPublicTree(child, all),
        )
    );
}
export const viewIsDue = (lastCounted: number | undefined, now: number) =>
    lastCounted === undefined || now - lastCounted >= 86_400_000;
export function requireVersion(current: string, supplied: string) {
    if (current !== supplied) throw new Error("concurrency_conflict");
}
export function validSlug(value: string) {
    return value.length <= 120 && /^[a-z0-9]+(-[a-z0-9]+)*$/.test(value);
}
