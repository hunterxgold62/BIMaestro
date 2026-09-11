export type Asset = { name: string; bytes: number; sha256: string };
export function validateAssets(value: unknown, total: number): Asset[] | null {
  if (value === undefined) return null;
  if (!Array.isArray(value) || value.length < 2 || value.length > 4096) throw new Error('Liste de fichiers invalide');
  const names = new Set<string>();
  const assets = value.map((item) => {
    if (!item || typeof item.name !== 'string' || !/^(index\.zip|tile-\d{5}\.glb\.gz)$/.test(item.name) || names.has(item.name) ||
      !Number.isSafeInteger(item.bytes) || item.bytes <= 0 || item.bytes > 48 * 1024 * 1024 || !/^[0-9a-f]{64}$/.test(item.sha256)) throw new Error('Fichier invalide');
    names.add(item.name); return { name: item.name, bytes: item.bytes, sha256: item.sha256 };
  });
  if (!names.has('index.zip') || assets.reduce((sum, item) => sum + item.bytes, 0) !== total || total > 512 * 1024 * 1024) throw new Error('Taille totale invalide');
  return assets;
}
export function exportPaths(item: { storage_path: string; manifest?: { assets?: Asset[] } }) {
  const assets = item.manifest?.assets;
  return assets?.length ? assets.map(asset => item.storage_path.replace(/index\.zip$/, asset.name)) : [item.storage_path];
}
