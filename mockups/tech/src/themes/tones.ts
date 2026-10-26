export const colorTones = ["blue", "orange", "red", "green", "purple"] as const;
export type ColorTone = (typeof colorTones)[number];
export type TonePalette = Record<
    ColorTone,
    { ink: string; bg: string; border: string }
>;

export const statusTones = {
    info: "blue",
    warning: "orange",
    error: "red",
    success: "green",
} as const satisfies Record<string, ColorTone>;

/** A tag keeps its color across pages, filtering and changes to list order. */
export function tagTone(slug: string): ColorTone {
    let hash = 0;
    for (const character of slug) {
        hash = (Math.imul(hash, 31) + character.codePointAt(0)!) >>> 0;
    }
    return colorTones[hash % colorTones.length];
}
