import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type { Root } from "mdast";

// Use complete opening paragraphs; headings, tables and code stay in the article.
function remarkFeedIntro() {
    return (tree: Root) => {
        tree.children = tree.children
            .filter(
                (node) =>
                    node.type === "paragraph" &&
                    node.children.some(
                        (child) =>
                            child.type !== "image" && child.type !== "html",
                    ),
            )
            .slice(0, 3);
    };
}

export function PostPreview({
    description,
    markdown,
}: {
    description: string;
    markdown: string;
}) {
    return (
        <div className="post-preview">
            {description && <p>{description}</p>}
            <ReactMarkdown
                remarkPlugins={[remarkGfm, remarkFeedIntro]}
                allowedElements={["p", "a", "strong", "em", "code", "br"]}
                unwrapDisallowed
                skipHtml
            >
                {markdown}
            </ReactMarkdown>
        </div>
    );
}
