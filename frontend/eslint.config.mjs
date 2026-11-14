import nextVitals from "eslint-config-next/core-web-vitals";

const config = [
  ...nextVitals,
  {
    settings: { next: { rootDir: ["apps/*/"] } },
    rules: {
      // UI text must come from the message catalog (packages/i18n), never hard-coded.
      "react/jsx-no-literals": ["error", { noStrings: true, ignoreProps: true, allowedStrings: ["·", "—", "/", "|"] }],
    },
  },
  { ignores: ["**/.next/**", "**/node_modules/**", "**/next-env.d.ts"] },
];

export default config;
