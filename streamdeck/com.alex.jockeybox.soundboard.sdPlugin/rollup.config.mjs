import commonjs from "@rollup/plugin-commonjs";
import nodeResolve from "@rollup/plugin-node-resolve";
import terser from "@rollup/plugin-terser";
import typescript from "@rollup/plugin-typescript";

const isWatching = !!process.env.ROLLUP_WATCH;

/**
 * The bundle is the whole plugin: `@elgato/streamdeck` is inlined so the
 * installed .sdPlugin folder needs no `node_modules` next to it.
 *
 * @type {import("rollup").RollupOptions}
 */
const config = {
  input: "src/plugin.ts",
  output: {
    file: "bin/plugin.js",
    format: "es",
    sourcemap: isWatching,
    inlineDynamicImports: true,
  },
  // Stream Deck's logger and the Node runtime are supplied by the host; nothing
  // from `node:*` should be bundled.
  external: [/^node:/],
  plugins: [
    {
      name: "watch-manifest",
      buildStart() {
        this.addWatchFile("manifest.json");
      },
    },
    typescript({ tsconfig: "./tsconfig.json" }),
    nodeResolve({
      browser: false,
      exportConditions: ["node"],
      preferBuiltins: true,
    }),
    commonjs(),
    !isWatching && terser({ format: { comments: false } }),
    {
      // The host launches bin/plugin.js as ESM; the package.json marker makes that
      // explicit even though the .sdPlugin folder is not an npm package.
      name: "emit-module-marker",
      generateBundle() {
        this.emitFile({ type: "asset", fileName: "package.json", source: '{ "type": "module" }\n' });
      },
    },
  ],
};

export default config;
