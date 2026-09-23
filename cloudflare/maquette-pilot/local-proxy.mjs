import http from 'node:http';

const endpoint = 'https://xqovxfgghbqxwsadzhzl.functions.supabase.co/mep-share';
const localOrigin = 'http://localhost:3001';
http.createServer(async (request, response) => {
  const cors = {
    'Access-Control-Allow-Origin': localOrigin,
    'Access-Control-Allow-Headers': 'content-type, apikey',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Cache-Control': 'no-store',
  };
  if (request.url !== '/mep-share') { response.writeHead(404, cors).end(); return; }
  if (request.method === 'OPTIONS') { response.writeHead(204, cors).end(); return; }
  if (request.method !== 'POST' || request.headers.origin !== localOrigin) { response.writeHead(403, cors).end(); return; }
  try {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const upstream = await fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', apikey: request.headers.apikey || '' }, body: Buffer.concat(chunks) });
    response.writeHead(upstream.status, { ...cors, 'Content-Type': 'application/json' });
    response.end(Buffer.from(await upstream.arrayBuffer()));
  } catch { response.writeHead(502, cors).end(JSON.stringify({ error: 'Pilote local indisponible' })); }
}).listen(3002, '127.0.0.1');
