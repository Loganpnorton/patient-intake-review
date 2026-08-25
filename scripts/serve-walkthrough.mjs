import { createReadStream } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'docs');
createServer((request, response) => {
  const file = request.url === '/synthetic-walkthrough.html' || request.url === '/'
    ? join(root, 'synthetic-walkthrough.html')
    : null;
  if (!file) {
    response.writeHead(404).end('Not found');
    return;
  }
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  createReadStream(file).pipe(response);
}).listen(9006, '127.0.0.1', () => console.log('Walkthrough: http://127.0.0.1:9006'));
