import { build } from "esbuild";
import { spawn } from "node:child_process";
import { copyFile, cp, mkdir, readFile, realpath, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import postject from "postject";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const require = createRequire(import.meta.url);

function run(command, args) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(command, args, {
      cwd: repoRoot,
      shell: false,
      windowsHide: true,
      stdio: "inherit",
    });
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (code === 0) resolveRun();
      else reject(new Error(`command failed (${signal ?? code}): ${command}`));
    });
  });
}

async function main() {
  const { values } = parseArgs({
    options: {
      runtime: { type: "string", default: `win-${process.arch}` },
      output: { type: "string" },
      help: { type: "boolean", short: "h" },
    },
  });
  if (values.help) {
    console.log(
      "Usage: node scripts/build-node.mjs [--runtime win-x64|win-arm64] [--output directory]",
    );
    return;
  }
  const runtime = values.runtime;
  if (!["win-x64", "win-arm64"].includes(runtime)) {
    throw new Error("--runtime must be win-x64 or win-arm64");
  }
  if (process.platform !== "win32" || process.versions.node.split(".")[0] !== "24") {
    throw new Error("Windows 版 Node.js 24 が必要です。");
  }
  if (runtime !== `win-${process.arch}`) {
    throw new Error(`対象と同じアーキテクチャの Node.js 24 が必要です: ${runtime}`);
  }
  const output = resolve(repoRoot, values.output ?? join("dist", runtime));
  const moduleRoot = resolve(repoRoot, "node_modules");
  const normalizedOutput = output.toLowerCase();
  const normalizedModules = moduleRoot.toLowerCase();
  if (
    normalizedOutput === repoRoot.toLowerCase() || normalizedOutput === normalizedModules
    || normalizedOutput.startsWith(`${normalizedModules}${sep}`)
  ) {
    throw new Error(
      "出力先にはリポジトリのルートと node_modules 以外のディレクトリを指定してください。",
    );
  }

  // Koffi 本体と、現在の Windows アーキテクチャ用のネイティブパッケージを同梱する。
  const koffiRoot = dirname(require.resolve("koffi"));
  const addonName = `@koromix/koffi-win32-${process.arch}`;
  const addonRoot = dirname(require.resolve(addonName));
  const koffiPackage = JSON.parse(await readFile(join(koffiRoot, "package.json"), "utf8"));
  const addonPackage = JSON.parse(await readFile(join(addonRoot, "package.json"), "utf8"));
  if (koffiPackage.version !== addonPackage.version) {
    throw new Error("Koffi とネイティブパッケージのバージョンが一致しません。");
  }

  const work = join(repoRoot, ".crer", "build", runtime);
  await mkdir(work, { recursive: true });
  await mkdir(output, { recursive: true });
  const bundled = join(work, "main.cjs");
  const blob = join(work, "sea-prep.blob");
  const configuration = join(work, "sea-config.json");
  const executable = join(output, "crer.exe");
  const sourceExecutable = await realpath(process.execPath);
  if (resolve(executable).toLowerCase() === sourceExecutable.toLowerCase()) {
    throw new Error("実行中の Node.js バイナリは出力先に指定できません。");
  }
  await build({
    absWorkingDir: repoRoot,
    entryPoints: ["src/main.ts"],
    outfile: bundled,
    bundle: true,
    platform: "node",
    target: "node24",
    format: "cjs",
    external: ["koffi"],
    logLevel: "info",
    banner: {
      // SEA の require を実行ファイル基準にし、外部の Koffi を読み込めるようにする。
      js:
        "if (require(\"node:sea\").isSea()) { require = require(\"node:module\").createRequire(process.execPath); }",
    },
  });
  await writeFile(
    configuration,
    JSON.stringify(
      {
        main: bundled,
        output: blob,
        disableExperimentalSEAWarning: true,
        useSnapshot: false,
        useCodeCache: false,
      },
      null,
      2,
    ),
  );
  await run(process.execPath, ["--experimental-sea-config", configuration]);
  await copyFile(sourceExecutable, executable);
  await postject.inject(executable, "NODE_SEA_BLOB", await readFile(blob), {
    sentinelFuse: "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2",
  });
  await cp(koffiRoot, join(output, "node_modules", "koffi"), { recursive: true, force: true });
  await cp(addonRoot, join(output, "node_modules", addonName), { recursive: true, force: true });
  console.log(
    `Wrote ${runtime} Node.js ${process.versions.node} distribution: ${
      relative(repoRoot, output) || output
    }`,
  );
}

main().catch((error) => {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
});
