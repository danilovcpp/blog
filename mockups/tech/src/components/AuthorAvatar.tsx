import { localId, shortAuthor } from "../mock/data";
import { useMock } from "../mock/store";

export function AuthorAvatar({
    authorId,
    className,
    large = false,
}: {
    authorId: string;
    className: string;
    large?: boolean;
}) {
    const { displayName, media } = useMock();
    const avatar = authorId === localId ? media.avatar : null;
    const name = authorId === localId ? displayName : shortAuthor(authorId);
    const initial = [...name.trim()][0]?.toLocaleUpperCase("ru") || "А";
    return (
        <span className={className} aria-hidden="true">
            {avatar ? (
                <img
                    className="author-avatar-image"
                    src={large ? avatar.url : avatar.smallUrl}
                    alt=""
                />
            ) : (
                initial
            )}
        </span>
    );
}
