import { readFile } from "node:fs/promises";
import { createServer } from "node:http";

const port = Number(process.argv[2] ?? "8080");
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error("fixture のポートは 1〜65535 の整数で指定してください。");
}
const fixture = await readFile(new URL("../fixtures/playback/index.html", import.meta.url));
const server = createServer((request, response) => {
  const pathname = new URL(request.url ?? "/", `http://127.0.0.1:${port}`).pathname;
  if (pathname !== "/" && pathname !== "/index.html") {
    response.writeHead(404);
    response.end("Not found");
    return;
  }
  response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
  response.end(fixture);
});
server.listen(port, "127.0.0.1", () => {
  console.log(`再生用 fixture: http://127.0.0.1:${port}/index.html`);
});
