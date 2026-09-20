// Emit a credential-free Cloudflare MCP execute argument. Does not deploy.
// Usage: node build-api-deployment.mjs [--enabled]
import { readFileSync } from 'node:fs';
const enabled = process.argv.includes('--enabled');
const config = JSON.parse(readFileSync(new URL('./wrangler.jsonc', import.meta.url), 'utf8'));
const metadata = {
  main_module: 'worker.js',
  compatibility_date: config.compatibility_date,
  compatibility_flags: config.compatibility_flags,
  observability: { enabled: false },
  bindings: [
    { name: 'CATALOG', type: 'd1', database_id: config.d1_databases[0].database_id },
    { name: 'FAMILIES', type: 'r2_bucket', bucket_name: config.r2_buckets[0].bucket_name },
    { name: 'LIBRARY_ENABLED', type: 'plain_text', text: String(enabled) }
  ]
};
const boundary = 'bimaestro-' + crypto.randomUUID();
const parts = [
  `--${boundary}`, 'Content-Disposition: form-data; name="metadata"',
  'Content-Type: application/json', '', JSON.stringify(metadata)
];
for (const file of ['worker.js', 'quota.js']) {
  parts.push(`--${boundary}`, `Content-Disposition: form-data; name="${file}"; filename="${file}"`,
    'Content-Type: application/javascript+module', '', readFileSync(new URL(file, import.meta.url), 'utf8'));
}
parts.push(`--${boundary}--`, '');
const payload = {
  method: 'PUT', path: '/accounts/ACCOUNT_ID/workers/scripts/' + config.name,
  body: parts.join('\r\n'), contentType: `multipart/form-data; boundary=${boundary}`, rawBody: true
};
const code = `async () => { const request = ${JSON.stringify(payload)}; request.path = request.path.replace('ACCOUNT_ID', accountId); return await cloudflare.request(request); }`;
process.stdout.write(JSON.stringify({ code }));
