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
  {
    // Vendored animate-ui / shadcn source (added with the shadcn CLI): keep it close to upstream, no app text in it.
    files: ["packages/ui/src/components/**", "packages/ui/src/hooks/**", "packages/ui/src/lib/get-strict-context.tsx"],
    linterOptions: { reportUnusedDisableDirectives: "off" },
    rules: {
      "react/jsx-no-literals": "off",
      "react-hooks/set-state-in-effect": "off",
      "react-hooks/immutability": "off",
      "react-hooks/static-components": "off",
      "react-hooks/refs": "off",
    },
  },
  { ignores: ["**/.next/**", "**/node_modules/**", "**/next-env.d.ts"] },
];

export default config;
