import { brand } from "../brands";
import type { TonePalette } from "./tones";
/** Brand packages are resolved at build time, never from visitor preferences. */
const photos = {
    workshopHero: "/images/retrotech-hero-v2.png",
    radio: "/images/workshop.png",
    deck: "/images/deck.png",
    circuit: "/images/circuit.png",
};
export const themes = {
    generic: {
        name: "Generic · Ant Design",
        colors: {
            bg: "#f5f5f5",
            surface: "#ffffff",
            ink: "#1f1f1f",
            muted: "#737373",
            line: "#e8e8e8",
            accent: "#1668dc",
            soft: "#e6f4ff",
            hero: "#f0f5ff",
            amber: "#d48806",
            "danger-fill": "#c5303e",
            "like-top": "#72c5ff",
            "like-middle": "#4096ff",
            "like-bottom": "#1677ff",
            "like-edge": "#e6f4ff",
            "like-shine": "#ffffff",
            "like-glow": "#4096ff",
            "like-ink": "#0958b8",
        },
        tones: {
            blue: { ink: "#0958b8", bg: "#e6f4ff", border: "#91caff" },
            orange: { ink: "#994b00", bg: "#fff7e6", border: "#ffd591" },
            red: { ink: "#b42332", bg: "#fff1f0", border: "#ffb6b3" },
            green: { ink: "#2f7012", bg: "#f6ffed", border: "#b7df94" },
            purple: { ink: "#6941a5", bg: "#f9f0ff", border: "#d3adf7" },
        } satisfies TonePalette,
        hero: photos.circuit,
        auth: photos.deck,
        heroAlt: "Иллюстрация публикации сообщества",
        authAlt: "Пространство для совместной работы",
        covers: [
            photos.circuit,
            photos.deck,
            photos.radio,
            photos.circuit,
            photos.deck,
            photos.radio,
        ],
    },
    workshop: {
        name: "Мастерская · лён и хвоя",
        colors: {
            bg: "#f5f4ef",
            surface: "#fffefa",
            ink: "#183332",
            muted: "#77817b",
            line: "#e2e5db",
            accent: "#276451",
            soft: "#eaf0e7",
            hero: "#f0eee4",
            amber: "#ba713e",
            "danger-fill": "#a34136",
            "like-top": "#fc9450",
            "like-middle": "#fa672f",
            "like-bottom": "#f6482d",
            "like-edge": "#fffaf4",
            "like-shine": "#fff7d6",
            "like-glow": "#fc8337",
            "like-ink": "#183332",
        },
        tones: {
            blue: { ink: "#365f78", bg: "#eaf0f3", border: "#c1d1dc" },
            orange: { ink: "#8d5028", bg: "#f7eddf", border: "#e3c7a4" },
            red: { ink: "#a34136", bg: "#f6e9e4", border: "#dfbbb1" },
            green: { ink: "#38634a", bg: "#eaf0e5", border: "#c1d3b9" },
            purple: { ink: "#705778", bg: "#f0eaf1", border: "#d4c3d8" },
        } satisfies TonePalette,
        hero: photos.workshopHero,
        auth: photos.radio,
        heroAlt:
            "Ретромастерская: приёмник «Сокол-304», осциллограф и инструменты на деревянном столе под тёплым светом лампы",
        authAlt: "Тёплый свет в мастерской старой электроники",
        covers: [
            photos.radio,
            photos.deck,
            photos.circuit,
            photos.circuit,
            photos.deck,
            photos.radio,
        ],
    },
    blueprint: {
        name: "Чертёж · бумага и индиго",
        colors: {
            bg: "#f0f3f6",
            surface: "#fcfdff",
            ink: "#20354e",
            muted: "#68798c",
            line: "#dce3ec",
            accent: "#365c85",
            soft: "#e8eef6",
            hero: "#e9eef5",
            amber: "#a46640",
            "danger-fill": "#a23f4d",
            "like-top": "#9acbf5",
            "like-middle": "#628fc7",
            "like-bottom": "#365c85",
            "like-edge": "#edf6ff",
            "like-shine": "#ffffff",
            "like-glow": "#729fda",
            "like-ink": "#365c85",
        },
        tones: {
            blue: { ink: "#365c85", bg: "#e8eef8", border: "#bdcde3" },
            orange: { ink: "#91502e", bg: "#f8ede4", border: "#e5c6af" },
            red: { ink: "#a23f4d", bg: "#f7e9ed", border: "#e4bdc7" },
            green: { ink: "#326957", bg: "#e7f1ed", border: "#b6d5c8" },
            purple: { ink: "#645289", bg: "#eeebf7", border: "#ccc2e1" },
        } satisfies TonePalette,
        hero: photos.circuit,
        auth: photos.deck,
        heroAlt:
            "Плата винтажного усилителя с конденсаторами и медными проводами",
        authAlt: "Кассетная дека на рабочем столе",
        covers: [
            photos.radio,
            photos.deck,
            photos.circuit,
            photos.circuit,
            photos.deck,
            photos.radio,
        ],
    },
    midnight: {
        name: "Ночная смена · графит и медь",
        colors: {
            bg: "#161f20",
            surface: "#202b2c",
            ink: "#e9ede3",
            muted: "#a1b2ac",
            line: "#364343",
            accent: "#a2d1b7",
            soft: "#2c3b36",
            hero: "#263430",
            amber: "#dfac7b",
            "danger-fill": "#b74d48",
            "like-top": "#ffe1a3",
            "like-middle": "#efad72",
            "like-bottom": "#d87b4b",
            "like-edge": "#ffe9c7",
            "like-shine": "#fff8e8",
            "like-glow": "#efad72",
            "like-ink": "#efc398",
        },
        tones: {
            blue: { ink: "#a6c8e4", bg: "#293944", border: "#476176" },
            orange: { ink: "#e6b78b", bg: "#40352b", border: "#755b42" },
            red: { ink: "#f0aaa0", bg: "#432f30", border: "#7a4d4b" },
            green: { ink: "#afd4b8", bg: "#2b3d33", border: "#506e5a" },
            purple: { ink: "#cbb8e2", bg: "#383244", border: "#665579" },
        } satisfies TonePalette,
        hero: photos.deck,
        auth: photos.circuit,
        heroAlt: "Винтажная кассетная дека в мастерской",
        authAlt: "Компоненты усилителя крупным планом",
        covers: [
            photos.radio,
            photos.deck,
            photos.circuit,
            photos.circuit,
            photos.deck,
            photos.radio,
        ],
    },
};
export type ThemeName = keyof typeof themes;
export const themeName = (import.meta.env.VITE_THEME ||
    (brand.id === "generic" ? "generic" : "workshop")) as ThemeName;
if (!(themeName in themes)) throw new Error(`Unknown VITE_THEME: ${themeName}`);
const imageKey = themeName === "generic" ? "blueprint" : themeName;
export const theme = {
    ...themes[themeName],
    hero: brand.images[imageKey][0],
    auth: brand.images[imageKey][1],
    covers: brand.images.covers,
    heroAlt:
        brand.id === "generic"
            ? brand.images.heroAlt
            : themes[themeName].heroAlt,
    authAlt:
        brand.id === "generic"
            ? brand.images.authAlt
            : themes[themeName].authAlt,
};
export function applyTheme() {
    document.documentElement.dataset.theme = themeName;
    document.documentElement.dataset.brand = brand.id;
    document.documentElement.dataset.presentation = brand.presentation;
    document
        .querySelector("meta[name=description]")
        ?.setAttribute("content", brand.about.intro);
    document.documentElement.style.colorScheme =
        themeName === "midnight" ? "dark" : "light";
    document
        .querySelector('meta[name="theme-color"]')
        ?.setAttribute("content", theme.colors.bg);
    for (const [key, value] of Object.entries(theme.colors))
        document.documentElement.style.setProperty(`--${key}`, value);
    for (const [tone, swatch] of Object.entries(theme.tones))
        for (const [key, value] of Object.entries(swatch))
            document.documentElement.style.setProperty(
                `--tone-${tone}-${key}`,
                value,
            );
}
