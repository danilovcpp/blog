import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
    const selected =
        process.env.VITE_THEME ||
        loadEnv(mode, process.cwd(), "VITE_").VITE_THEME ||
        "paper";
    if (!["paper", "forest", "midnight"].includes(selected))
        throw new Error(
            `Unknown VITE_THEME: ${selected}. Use paper, forest or midnight.`,
        );
    return { plugins: [react()], server: { port: 5173, strictPort: true } };
});
