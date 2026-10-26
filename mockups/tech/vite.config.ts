import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import { brands } from "./src/brands/configs.ts";

export default defineConfig(({ mode }) => {
    const env = loadEnv(mode, process.cwd(), "VITE_");
    const selected = process.env.VITE_THEME || env.VITE_THEME || "workshop";
    if (!["workshop", "blueprint", "midnight", "generic"].includes(selected))
        throw new Error(
            `Unknown VITE_THEME: ${selected}. Use workshop, blueprint, midnight or generic.`,
        );
    const brandKey =
        process.env.VITE_BRAND ||
        env.VITE_BRAND ||
        (selected === "generic" ? "generic" : "retrotech");
    if (!(brandKey in brands))
        throw new Error(`Unknown VITE_BRAND: ${brandKey}`);
    const brand = brands[brandKey as keyof typeof brands];
    const escape = (value: string) =>
        value
            .replaceAll("&", "&amp;")
            .replaceAll('"', "&quot;")
            .replaceAll("<", "&lt;");
    return {
        plugins: [
            react(),
            {
                name: "static-brand",
                transformIndexHtml(html: string) {
                    return html
                        .replace(
                            /<title>.*?<\/title>/,
                            `<title>${escape(brand.name)} — ${escape(brand.documentTitle)}</title>`,
                        )
                        .replace(
                            /(<meta name="description" content=")[^"]*/,
                            `$1${escape(brand.about.intro)}`,
                        )
                        .replace(
                            'href="/favicon.svg"',
                            brand.logo === "cpu"
                                ? 'href="/favicon.svg"'
                                : 'href="/favicon-book.svg"',
                        );
                },
            },
        ],
        server: { host: "127.0.0.1", port: 5174, strictPort: true },
    };
});
