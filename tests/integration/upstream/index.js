import { createServer } from 'node:http';

const PORT = parseInt(process.env.PORT ?? '3000', 10);

const server = createServer((req, res) => {
  const stylobotHeaders = {};
  for (const [key, value] of Object.entries(req.headers)) {
    if (key.startsWith('x-stylobot-')) {
      stylobotHeaders[key] = value;
    }
  }

  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({
    method: req.method,
    url: req.url,
    headers: req.headers,
    stylobot: stylobotHeaders,
    timestamp: new Date().toISOString(),
  }));
});

server.listen(PORT, () => {
  console.log(`Upstream echo server listening on port ${PORT}`);
});
