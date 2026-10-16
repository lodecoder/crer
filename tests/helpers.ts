import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { open } from "node:fs/promises";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";

export async function temporaryFile(): Promise<string> {
  const path = join(tmpdir(), `crer-test-${randomUUID()}`);
  const file = await open(path, "wx");
  await file.close();
  return path;
}

export async function createTestServer(
  handler: (request: Request) => Response | Promise<Response>,
) {
  const server = createServer(async (request, response) => {
    try {
      const url = new URL(request.url ?? "/", `http://${request.headers.host}`);
      const result = await handler(new Request(url));
      response.writeHead(result.status, Object.fromEntries(result.headers));
      response.end(Buffer.from(await result.arrayBuffer()));
    } catch (error) {
      response.writeHead(500);
      response.end(String(error));
    }
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", reject);
      resolve();
    });
  });
  return {
    port: (server.address() as AddressInfo).port,
    close: () =>
      new Promise<void>((resolve, reject) => {
        server.close((error) => error ? reject(error) : resolve());
        server.closeAllConnections();
      }),
  };
}

export async function runCli(
  args: string[],
  options: { env?: NodeJS.ProcessEnv; signal?: AbortSignal } = {},
) {
  const child = spawn(process.execPath, ["--import", "tsx", "src/main.ts", ...args], {
    cwd: process.cwd(),
    env: { ...process.env, ...options.env },
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
    signal: options.signal,
  });
  const stdout: Buffer[] = [];
  const stderr: Buffer[] = [];
  child.stdout.on("data", (chunk: Buffer) => stdout.push(chunk));
  child.stderr.on("data", (chunk: Buffer) => stderr.push(chunk));
  const code = await new Promise<number>((resolve, reject) => {
    child.once("error", reject);
    child.once("close", (code) => resolve(code ?? 1));
  });
  return { code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr) };
}
