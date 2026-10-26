import test from "node:test";
import assert from "node:assert/strict";
import {
    matchesFeedFilters,
    commentInPublicTree,
    passwordProblem,
    viewIsDue,
    requireVersion,
    validSlug,
    scalarLength,
    trimText,
} from "../src/mock/rules.ts";

test("feed combines ANY category with ALL tags, and empty selection is unrestricted", () => {
    const post = { categoryIds: ["design"], tagIds: ["attention", "practice"] };
    assert.equal(
        matchesFeedFilters(
            post,
            ["nature", "design"],
            ["attention", "practice"],
        ),
        true,
    );
    assert.equal(matchesFeedFilters(post, ["nature"], ["attention"]), false);
    assert.equal(
        matchesFeedFilters(post, ["design"], ["attention", "unknown"]),
        false,
    );
    assert.equal(matchesFeedFilters(post, [], []), true);
});
test("a hidden/deleted ancestor remains reachable only with visible descendants", () => {
    const comments = [
        { id: "root", parentId: null, state: "Deleted" },
        { id: "middle", parentId: "root", state: "Hidden" },
        { id: "leaf", parentId: "middle", state: "Visible" },
        { id: "empty", parentId: null, state: "Hidden" },
    ];
    assert.equal(commentInPublicTree(comments[0], comments), true);
    assert.equal(commentInPublicTree(comments[1], comments), true);
    assert.equal(commentInPublicTree(comments[3], comments), false);
    assert.equal(
        commentInPublicTree(
            comments[0],
            comments.filter((c) => c.id !== "leaf"),
        ),
        false,
    );
});
test("password policy preserves spaces, counts NFKC code points and does not invent complexity classes", () => {
    assert.equal(passwordProblem("long simple passphrase"), "");
    assert.equal(passwordProblem("a".repeat(15)), "");
    assert.notEqual(passwordProblem("a".repeat(14)), "");
    assert.equal(passwordProblem(" ".repeat(15)), "");
    assert.notEqual(passwordProblem("a".repeat(129)), "");
    assert.equal(passwordProblem("😀".repeat(15)), "");
    assert.equal(passwordProblem("😀".repeat(128)), ""); // 128 scalars and exactly 512 UTF-8 bytes.
    assert.notEqual(passwordProblem("😀".repeat(129)), "");
});
test("fixed view window does not count early, equality counts again", () => {
    const t = 1_000;
    assert.equal(viewIsDue(undefined, t), true);
    assert.equal(viewIsDue(t, t + 86_399_999), false);
    assert.equal(viewIsDue(t, t + 86_400_000), true);
});
test("stale edit version fails instead of overwriting", () => {
    assert.doesNotThrow(() => requireVersion("version-2", "version-2"));
    assert.throws(
        () => requireVersion("version-2", "version-1"),
        /concurrency_conflict/,
    );
});
test("slug is canonical and first-party scalar counts do not use UTF16 length", () => {
    assert.equal(validSlug("a-post-2026"), true);
    for (const slug of ["Hello", "привет", "a--b", " a", "a-", "a".repeat(121)])
        assert.equal(validSlug(slug), false);
    assert.equal(scalarLength("😀".repeat(200)), 200);
    assert.throws(() => scalarLength("hello\u0000"));
    assert.throws(() => scalarLength("\ud800"));
    assert.equal(trimText("\u0085 title \u3000"), "title");
});
