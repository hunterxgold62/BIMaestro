const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname,'..');
const source = path.join(root,'tmp/mep-browser-publish/wwwroot/mep-engine');
const site = path.resolve(process.argv[2] || path.join(root,'.sites/bimaestro-viewer-mep-audit'));
if (!fs.existsSync(path.join(source,'_framework/blazor.boot.json'))) throw Error('Publish the MEP browser project before synchronizing.');
const boot=JSON.parse(fs.readFileSync(path.join(source,'_framework/blazor.boot.json'),'utf8'));
const currentWasm=new Set(Object.keys(boot.resources.fingerprinting).filter(name=>name.endsWith('.wasm')));
function copy(from,to) {
  fs.mkdirSync(to,{recursive:true});
  for(const entry of fs.readdirSync(from,{withFileTypes:true})) {
    if(/\.(br|gz|pdb)$/.test(entry.name)) continue;
    if(entry.name.endsWith('.wasm') && !currentWasm.has(entry.name)) continue;
    if(entry.isDirectory()) copy(path.join(from,entry.name),path.join(to,entry.name));
    else fs.copyFileSync(path.join(from,entry.name),path.join(to,entry.name));
  }
}
copy(source,path.join(site,'public/mep-engine'));
const framework=path.join(site,'public/mep-engine/_framework');
for(const name of fs.readdirSync(framework)) if(name.endsWith('.wasm') && !currentWasm.has(name)) fs.unlinkSync(path.join(framework,name));
const files=['GameMepData.cs','GameMepImpact.cs','GameMepCalculation.cs','GameMepDirectionExplanation.cs','GameMepDiagnostics.cs'];
const hashes=Object.fromEntries(files.map(file=>[file,crypto.createHash('sha256').update(fs.readFileSync(path.join(root,'BIMaestro/commands/Jeux vidéos/Maquette jouable',file))).digest('hex')]));
fs.writeFileSync(path.join(site,'public/mep-engine/source-manifest.json'),JSON.stringify({engineVersion:'mep-topology-2.0.1',hashes},null,2));
console.log('Shared MEP runtime synchronized with source fingerprints.');
