// Génère index.html + compositions/reel.html à partir des horodatages du défilé.
// Relancer après avoir changé la liste ou les durées : node gen.mjs
import { writeFileSync } from "node:fs";

const W = 0.35; // durée du balayage
// [nom, durée, étiquette, rush, n° de variante dans le rush]
const shots = [
  ["Revit de base", 1.8, "Avant", "effets", 0],
  ["Icônes métiers", 1.6, "01", "icones", 1],
  ["Pastilles", 1.2, "02", "effets", 1],
  ["Badges", 1.2, "03", "effets", 2],
  ["Icônes + texte coloré", 1.3, "04", "icones", 3],
  ["Texte en couleur", 1.2, "05", "effets", 4],
  ["Dégradés", 1.2, "06", "effets", 5],
  ["Fonds par dossier", 1.2, "07", "effets", 6],
  ["Icônes + dégradés", 1.3, "08", "icones", 4],
  ["Gras coloré", 1.2, "09", "effets", 9],
  ["Souligné", 1.2, "10", "effets", 10],
  ["Verre dépoli", 1.2, "11", "effets", 11],
  ["Plan d'architecte", 1.2, "12", "effets", 3],
  ["Dégradé animé", 1.3, "13", "effets", 13],
  ["Icônes de nuit", 1.8, "14", "icones", 5],
];
const HOOK = 2.2, OUTRO = 2.8, CARD_IN = 0.5;
let t = CARD_IN;
const items = shots.map(([name, dur, tag, rush, vk], k) => {
  const src = 1.1 + 5 * vk + 2.0;           // milieu stable de la variante dans le rush
  const start = k === 0 ? 0 : t - W;        // chevauche le plan précédent pendant le balayage
  const len = k === 0 ? t + dur : dur + W;
  const it = { k, name, tag, rush, start: +start.toFixed(2), len: +len.toFixed(2), media: +(k === 0 ? src - CARD_IN : src - W).toFixed(2), reveal: +(t - W).toFixed(2), show: +t.toFixed(2), dur };
  t += dur;
  return it;
});
const REEL = +(t + 0.4).toFixed(2);
const TOTAL = +(HOOK + REEL + OUTRO).toFixed(2);
const esc = (s) => s.replace(/&/g, "&amp;").replace(/'/g, "&#39;");

const videos = items.map((it) =>
  `      <video id="rv${it.k}" class="clip rv" src="assets/${it.rush}.mp4" data-start="${it.start}" data-duration="${it.len}" data-media-start="${it.media}" muted playsinline style="z-index:${it.k + 1}"></video>`).join("\n");
const labels = items.map((it) =>
  `    <div id="rl${it.k}" class="rl"><span class="rl-tag">${esc(it.tag)}</span><div class="rl-name">${esc(it.name)}</div></div>`).join("\n");
const tweens = items.map((it) => {
  const a = [];
  if (it.k > 0) {
    a.push(`tl.fromTo("#rv${it.k}", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${it.reveal});`);
    a.push(`tl.fromTo("#rline", { y: 0, opacity: 1 }, { y: 1000, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${it.reveal});`);
    a.push(`tl.to("#rline", { opacity: 0, duration: 0.04 }, ${+(it.reveal + W).toFixed(2)});`);
  }
  a.push(`tl.fromTo("#rl${it.k}", { opacity: 0, y: 26 }, { opacity: 1, y: 0, duration: 0.25, ease: "expo.out" }, ${it.k === 0 ? 0.3 : it.reveal + 0.1});`);
  const end = it.show + it.dur;
  if (it.k < items.length - 1) a.push(`tl.to("#rl${it.k}", { opacity: 0, y: -20, duration: 0.15, ease: "power2.in" }, ${+(end - W).toFixed(2)});`);
  return "    " + a.join("\n    ");
}).join("\n");

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    #rcard { position: absolute; left: 330px; top: 260px; width: 620px; height: 1000px; border-radius: 28px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
    .rv { position: absolute; left: 0; top: 0; width: 620px; height: 1469px; object-fit: fill; }
    #rline { position: absolute; left: 0; right: 0; top: 0; height: 8px; background: #0F5132; box-shadow: 0 0 24px #7FD6A6; z-index: 50; opacity: 0; }
    #rbar { position: absolute; left: 0; top: 0; bottom: 0; width: 10px; background: #0F5132; z-index: 51; }
    .rl { position: absolute; left: 0; width: 330px; top: 600px; padding: 0 24px 0 50px; opacity: 0; }
    .rl-tag { display: inline-block; color: #fff; background: #0F5132; font-weight: 900; font-size: 30px; padding: 6px 16px; border-radius: 999px; margin-bottom: 14px; }
    .rl-name { color: #111827; font-weight: 900; font-size: 44px; letter-spacing: -0.03em; line-height: 1.05; }
    #rtitle { position: absolute; top: 180px; left: 120px; right: 180px; text-align: center; color: #111827; font-weight: 900; font-size: 44px; letter-spacing: -0.02em; opacity: 0; }
    #rtitle b { color: #0F5132; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1920">
    <div id="rtitle">Apparence <b>BIMaestro</b></div>
    <div id="rcard">
${videos}
      <div id="rline"></div>
      <div id="rbar"></div>
    </div>
${labels}
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    tl.fromTo("#rcard", { x: 900, rotation: 6 }, { x: 0, rotation: 0, duration: ${CARD_IN}, ease: "expo.out" }, 0);
    tl.fromTo("#rtitle", { opacity: 0, y: 20 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, 0.2);
${tweens}
    tl.to(["#rcard", "#rl${items.length - 1}", "#rtitle"], { x: -1100, rotation: -6, duration: 0.4, ease: "expo.in" }, ${+(REEL - 0.4).toFixed(2)});
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

// Sons : pop sur l'accroche, clic sur chaque balayage, swoosh sur les entrées/sorties.
const sfx = [["pop", 0.4], ["whoosh", HOOK - 0.3]];
items.slice(1).forEach((it) => sfx.push(["click", +(HOOK + it.reveal).toFixed(2)]));
sfx.push(["whoosh", +(HOOK + REEL - 0.4).toFixed(2)], ["pop", +(HOOK + REEL + 0.1).toFixed(2)]);
const audio = sfx.map(([n, s], i) =>
  `      <audio id="sx${i}" class="clip" src="assets/sfx/${n}.mp3" data-start="${s}" data-duration="${n === "click" ? 0.37 : 0.6}" data-track-index="${3 + (i % 2)}" data-volume="${n === "click" ? 0.6 : 0.8}"></audio>`).join("\n");

const index = `<!doctype html>
<html lang="fr" data-composition-variables='[{"id":"hook1","type":"string","label":"Accroche ligne 1","default":"1 arborescence."},{"id":"hook2","type":"string","label":"Accroche ligne 2","default":"14 looks."}]'>
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1080, height=1920" />
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.15.0/dist/gsap.min.js"></script>
    <style>
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { width: 1080px; height: 1920px; overflow: hidden; background: #FFFFFF; }
      #root { position: relative; width: 100%; height: 100%; font-family: Inter; }
      #bg { position: absolute; inset: 0; background: #FFFFFF; }
      #bg-dots { position: absolute; inset: 0; background-image: radial-gradient(#E5E7EB 2px, transparent 2px); background-size: 40px 40px; opacity: .7; }
    </style>
  </head>
  <body>
    <div id="root" data-composition-id="main" data-start="0" data-duration="${TOTAL}" data-width="1080" data-height="1920">
      <div id="bg"></div>
      <div id="bg-dots"></div>
      <div id="hook" data-composition-id="hook" data-composition-src="compositions/hook.html" data-start="0" data-duration="${HOOK}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
      <div id="reel" data-composition-id="reel" data-composition-src="compositions/reel.html" data-start="${HOOK}" data-duration="${REEL}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
      <div id="outro" data-composition-id="outro" data-composition-src="compositions/outro.html" data-start="${+(HOOK + REEL).toFixed(2)}" data-duration="${OUTRO}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
${audio}
    </div>
    <script>
      window.__timelines["main"] = gsap.timeline({ paused: true });
    </script>
  </body>
</html>
`;
writeFileSync("index.html", index);
console.log(`total ${TOTAL}s, reel ${REEL}s, ${items.length} plans`);
