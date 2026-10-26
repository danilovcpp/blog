export const themes = {
    paper: {
        name: "Бумага и шалфей",
        colors: {
            bg: "#f8f9f5",
            surface: "#ffffff",
            ink: "#252e26",
            muted: "#73796f",
            line: "#e0e4d9",
            accent: "#47634e",
            soft: "#eaf0e4",
            hero: "#e4eadd",
        },
        hero: "/images/interior.jpg",
        auth: "/images/forest.jpg",
        heroAlt: "Натуральные материалы и белая ваза в спокойном интерьере",
        authAlt: "Туман поднимается над лесом",
        covers: [
            "/images/interior.jpg",
            "/images/desk.jpg",
            "/images/mountains.jpg",
            "/images/ceramics.jpg",
            "/images/forest.jpg",
            "/images/coast.jpg",
        ],
    },
    forest: {
        name: "Лесной дневник",
        colors: {
            bg: "#f3f5ed",
            surface: "#fcfdf8",
            ink: "#1c362c",
            muted: "#66766a",
            line: "#d5dfcf",
            accent: "#315c46",
            soft: "#e1e9d8",
            hero: "#d9e5cc",
        },
        hero: "/images/forest.jpg",
        auth: "/images/mountains.jpg",
        heroAlt: "Туман окутывает горный хвойный лес",
        authAlt: "Горное озеро и заснеженные вершины",
        covers: [
            "/images/forest.jpg",
            "/images/interior.jpg",
            "/images/coast.jpg",
            "/images/desk.jpg",
            "/images/mountains.jpg",
            "/images/hero-architecture.jpg",
        ],
    },
    midnight: {
        name: "После полуночи",
        colors: {
            bg: "#181e27",
            surface: "#222b36",
            ink: "#eeeee7",
            muted: "#aab2bd",
            line: "#37424f",
            accent: "#c9d9b5",
            soft: "#2c3b36",
            hero: "#2a3a34",
        },
        hero: "/images/coast.jpg",
        auth: "/images/interior.jpg",
        heroAlt: "Белая полоса прибоя на берегу моря",
        authAlt: "Спокойная интерьерная композиция с керамикой",
        covers: [
            "/images/coast.jpg",
            "/images/hero-architecture.jpg",
            "/images/forest.jpg",
            "/images/mountains.jpg",
            "/images/desk.jpg",
            "/images/interior.jpg",
        ],
    },
};

export type ThemeName = keyof typeof themes;
const selected = (import.meta.env.VITE_THEME || "paper") as ThemeName;
if (!(selected in themes)) throw new Error(`Unknown VITE_THEME: ${selected}`);
export const theme = themes[selected];
export const themeName = selected;

export function applyTheme() {
    document.documentElement.dataset.theme = selected;
    document.documentElement.style.colorScheme =
        selected === "midnight" ? "dark" : "light";
    document
        .querySelector('meta[name="theme-color"]')
        ?.setAttribute("content", theme.colors.bg);
    for (const [key, value] of Object.entries(theme.colors))
        document.documentElement.style.setProperty(`--${key}`, value);
}
