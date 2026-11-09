/**
 * `npm run checks` — bundles the Stream-Deck-free parts of the plugin so they can
 * be exercised in plain Node. This is a development aid, not part of the shipped
 * bundle: nothing here is referenced by manifest.json.
 */
import nodeResolve from "@rollup/plugin-node-resolve";
import typescript from "@rollup/plugin-typescript";

const plugins = () => [
  typescript({ tsconfig: "./tsconfig.json" }),
  nodeResolve({ preferBuiltins: true }),
];

/** @type {import("rollup").RollupOptions[]} */
export default ["render", "bridge", "api", "config"].map((name) => ({
  input: `src/${name}.ts`,
  output: { file: `checks/.build/${name}.mjs`, format: "es" },
  plugins: plugins(),
}));
