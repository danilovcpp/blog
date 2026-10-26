import { useId } from "react";

type PostIconName = "heart" | "views" | "comments" | "bookmark" | "link";

const heartPath =
    "M20.25 4.25C17.75 1.75 14.25 2.1 12 4.8C9.75 2.1 6.25 1.75 3.75 4.25C1.25 6.75 1.55 10.75 4.15 13.5L10.55 20.15Q12 21.6 13.45 20.15L19.85 13.5C22.45 10.75 22.75 6.75 20.25 4.25Z";

/** One optical grid for publication actions, including the taller eye shape. */
export function PostIcon({
    name,
    active = false,
}: {
    name: PostIconName;
    active?: boolean;
}) {
    const gradientId = `post-heart-${useId()}`;
    return (
        <span
            className={`post-icon post-icon-${name}${active ? " is-active" : ""}`}
            aria-hidden="true"
        >
            <svg
                width="24"
                height="24"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.8"
                strokeLinecap="round"
                strokeLinejoin="round"
                focusable="false"
            >
                {name === "heart" && (
                    <>
                        {active && (
                            <defs>
                                <linearGradient
                                    id={gradientId}
                                    x1="20"
                                    y1="3"
                                    x2="5"
                                    y2="21"
                                    gradientUnits="userSpaceOnUse"
                                >
                                    <stop stopColor="var(--like-top)" />
                                    <stop
                                        offset=".52"
                                        stopColor="var(--like-middle)"
                                    />
                                    <stop
                                        offset="1"
                                        stopColor="var(--like-bottom)"
                                    />
                                </linearGradient>
                            </defs>
                        )}
                        <path
                            d={heartPath}
                            fill={active ? `url(#${gradientId})` : "none"}
                            stroke={
                                active ? "var(--like-edge)" : "currentColor"
                            }
                            strokeWidth={active ? 1.25 : undefined}
                        />
                        {active && (
                            <path
                                className="post-heart-shine"
                                d="M4.5 8.5C4.5 5.2 8 3.8 10.1 6M14 5.7C16.2 3.8 19.5 5 19.5 8"
                                stroke="var(--like-shine)"
                                strokeWidth="1.2"
                            />
                        )}
                    </>
                )}
                {name === "views" && (
                    <>
                        <path d="M2.25 12C4.5 6.2 8 3.25 12 3.25S19.5 6.2 21.75 12C19.5 17.8 16 20.75 12 20.75S4.5 17.8 2.25 12Z" />
                        <circle cx="12" cy="12" r="3.4" />
                    </>
                )}
                {name === "comments" && (
                    <path d="M21 11.7A8.7 8.7 0 0 1 8.4 19.5L3 21L4.5 15.6A8.7 8.7 0 1 1 21 11.7Z" />
                )}
                {name === "bookmark" && (
                    <path
                        d="M6 3H18V21L12 17.2L6 21V3Z"
                        fill={active ? "currentColor" : "none"}
                    />
                )}
                {name === "link" && (
                    <>
                        <path d="M10 13A5 5 0 0 0 17.54 13.54L20.54 10.54A5 5 0 0 0 13.47 3.47L11.75 5.18M14 11A5 5 0 0 0 6.46 10.46L3.46 13.46A5 5 0 0 0 10.53 20.53L12.24 18.82" />
                    </>
                )}
            </svg>
        </span>
    );
}
