import { Be_Vietnam_Pro } from "next/font/google";

/** Be Vietnam Pro: designed for Vietnamese diacritics, clean for Latin text; exposed as --font-app. */
export const appFont = Be_Vietnam_Pro({
  subsets: ["latin", "vietnamese"],
  weight: ["400", "500", "600", "700"],
  variable: "--font-app",
  display: "swap",
});
