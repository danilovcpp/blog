import { useEffect, useRef, useState } from "react";
import "./typewriter.css";

export function TypewriterText({ phrases }: { phrases: readonly string[] }) {
    const [text, setText] = useState("");
    const element = useRef<HTMLElement>(null);

    useEffect(() => {
        if (!phrases.length) return;

        const motion = window.matchMedia("(prefers-reduced-motion: reduce)");
        let timer: ReturnType<typeof setTimeout> | undefined;
        let visible = false;
        let remaining: number[] = [];
        const nextPhrase = (previous: number) => {
            if (!remaining.length) {
                remaining = phrases.map((_, index) => index);
                for (let i = remaining.length - 1; i > 0; i--) {
                    const j = Math.floor(Math.random() * (i + 1));
                    [remaining[i], remaining[j]] = [remaining[j], remaining[i]];
                }
                // Pop from the shuffled bag; avoid a repeat between rounds.
                const last = remaining.length - 1;
                if (remaining.length > 1 && remaining[last] === previous)
                    [remaining[0], remaining[last]] = [
                        remaining[last],
                        remaining[0],
                    ];
            }
            return remaining.pop()!;
        };
        let phraseIndex = nextPhrase(-1);
        let length = 0;
        let deleting = false;

        const schedule = (delay: number) => {
            if (visible && !document.hidden && !motion.matches)
                timer = setTimeout(tick, delay);
        };
        const tick = () => {
            const phrase = phrases[phraseIndex];
            length += deleting ? -1 : 1;
            setText(phrase.slice(0, length));

            if (!deleting && length === phrase.length) {
                deleting = true;
                schedule(2400);
            } else if (deleting && length === 0) {
                deleting = false;
                phraseIndex = nextPhrase(phraseIndex);
                schedule(450);
            } else {
                schedule(deleting ? 30 : 65);
            }
        };
        const sync = () => {
            clearTimeout(timer);
            if (motion.matches) {
                setText(phrases[0]);
                return;
            }
            setText(phrases[phraseIndex].slice(0, length));
            schedule(length === 0 ? 450 : deleting ? 2400 : 65);
        };
        const observer = new IntersectionObserver(([entry]) => {
            visible = entry.isIntersecting;
            sync();
        });

        if (element.current) observer.observe(element.current);
        motion.addEventListener("change", sync);
        document.addEventListener("visibilitychange", sync);
        sync();

        return () => {
            clearTimeout(timer);
            observer.disconnect();
            motion.removeEventListener("change", sync);
            document.removeEventListener("visibilitychange", sync);
        };
    }, [phrases]);

    return (
        <i className="typewriter" ref={element}>
            <span className="sr-only">{phrases[0]}</span>
            <span className="typewriter-visual" aria-hidden="true">
                <span className="typewriter-sizer">
                    {phrases.map((phrase) => (
                        <span key={phrase}>{phrase}</span>
                    ))}
                </span>
                <span className="typewriter-line">
                    {text}
                    <span className="typewriter-cursor" />
                </span>
            </span>
        </i>
    );
}
