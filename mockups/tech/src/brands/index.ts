import { brands } from "./configs";
export { brands } from "./configs";
export type BrandName = keyof typeof brands;
export const brandName = (import.meta.env.VITE_BRAND ||
    (import.meta.env.VITE_THEME === "generic"
        ? "generic"
        : "retrotech")) as BrandName;
if (!(brandName in brands)) throw new Error(`Unknown VITE_BRAND: ${brandName}`);
export const brand = brands[brandName];
