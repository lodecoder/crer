import { spawn } from "node:child_process";
import { createServer } from "node:net";
import { Readable } from "node:stream";

export type ProcessOutput = "null" | "piped" | "inherit";
export type ProcessOptions = {
  args?: readonly string[];
  env?: NodeJS.ProcessEnv;
  stdout?: ProcessOutput;
  stderr?: ProcessOutput;
};
export type ProcessStatus = { code: number; signal: NodeJS.Signals | null };
export type LaunchedProcess = {
  pid: number;
  status: Promise<ProcessStatus>;
  stdout: ReadableStream<Uint8Array>;
  stderr: ReadableStream<Uint8Array>;
  kill(signal?: NodeJS.Signals): void;
};

function outputMode(value: ProcessOutput = "piped"): "ignore" | "pipe" | "inherit" {
  return value === "null" ? "ignore" : value === "piped" ? "pipe" : "inherit";
}

function webOutput(stream: Readable | null): ReadableStream<Uint8Array> {
  return stream
    ? Readable.toWeb(stream) as ReadableStream<Uint8Array>
    : new ReadableStream<Uint8Array>({ start: (controller) => controller.close() });
}

/** 起動失敗を呼び出し元へ返し、終了状態は出力の読み取りと並行して監視する。 */
export async function launchProcess(
  command: string,
  options: ProcessOptions = {},
): Promise<LaunchedProcess> {
  const child = spawn(command, [...(options.args ?? [])], {
    env: { ...process.env, ...options.env },
    shell: false,
    windowsHide: true,
    stdio: ["ignore", outputMode(options.stdout), outputMode(options.stderr)],
  });
  const status = new Promise<ProcessStatus>((resolve, reject) => {
    child.once("error", reject);
    child.once("exit", (code, signal) => resolve({ code: code ?? 1, signal }));
  });
  // 起動失敗時には status が返却されないため、その拒否も直ちに処理しておく。
  void status.catch(() => {});
  await new Promise<void>((resolve, reject) => {
    child.once("spawn", resolve);
    child.once("error", reject);
  });
  if (child.pid === undefined) throw new Error(`process started without a pid: ${command}`);
  return {
    pid: child.pid,
    status,
    stdout: webOutput(child.stdout),
    stderr: webOutput(child.stderr),
    kill: (signal = "SIGTERM") => {
      child.kill(signal);
    },
  };
}

async function collect(stream: ReadableStream<Uint8Array>): Promise<Uint8Array> {
  const chunks: Uint8Array[] = [];
  let length = 0;
  const reader = stream.getReader();
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      length += value.length;
    }
  } finally {
    reader.releaseLock();
  }
  const output = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    output.set(chunk, offset);
    offset += chunk.length;
  }
  return output;
}

/** 両方の出力を同時に読み取り、パイプの容量による子プロセスの停止を防ぐ。 */
export async function runProcess(
  command: string,
  options: ProcessOptions = {},
): Promise<ProcessStatus & { stdout: Uint8Array; stderr: Uint8Array }> {
  const child = await launchProcess(command, options);
  const [status, stdout, stderr] = await Promise.all([
    child.status,
    collect(child.stdout),
    collect(child.stderr),
  ]);
  return { ...status, stdout, stderr };
}

/** OS が割り当てたループバックポートを、リスナーの解放後に返す。 */
export function reserveLocalPort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen({ host: "127.0.0.1", port: 0, exclusive: true }, () => {
      const address = server.address();
      if (!address || typeof address === "string") {
        server.close(() => reject(new Error("could not reserve a local TCP port")));
        return;
      }
      server.close((error) => error ? reject(error) : resolve(address.port));
    });
  });
}
