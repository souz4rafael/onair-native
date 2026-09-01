import commonjs from "@rollup/plugin-commonjs";
import resolve from "@rollup/plugin-node-resolve";
import terser from "@rollup/plugin-terser";
import typescript from "@rollup/plugin-typescript";

const isWatch = !!process.env.ROLLUP_WATCH;

export default {
    input: "src/plugin.ts",
    output: {
        file: "com.souz4rafael.onair.sdPlugin/bin/plugin.js",
        format: "cjs",
        sourcemap: isWatch,
        sourcemapPathTransform: (path) => `../../${path}`,
    },
    plugins: [
        typescript({ tsconfig: "./tsconfig.json", sourceMap: isWatch }),
        resolve({ preferBuiltins: true }),
        commonjs(),
        !isWatch && terser(),
    ],
};
