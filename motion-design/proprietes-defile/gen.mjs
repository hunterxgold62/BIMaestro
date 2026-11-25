// « Le défilé » — LinkedIn 4:5 (1080×1350). Une grande palette Propriétés qui change de style
// à chaque balayage vert, puis une mosaïque « Choisis ton style ».
// Images : capture/rushes/styles (look k pris à 4,5+5k s, recadré sous l'en-tête de type).
// node gen.mjs → index.html + compositions/reel.html
import { writeFileSync } from "node:fs";

const W = 0.3, FIRST = 2.0, STEP = 1.6;
// [image, nom du look]
const looks = [
  ["s1", "Liserés"], ["s2", "Texte coloré"], ["s3", "Dégradé + pastille"], ["s4", "Titres + icônes"],
  ["s5", "Lignes alternées"], ["s6", "Contours"], ["s7", "Tes favoris en haut"], ["s8", "Tout en un"],
];
const items = looks.map(([img, name], k) => ({ k, img, name, t: +(FIRST + k * STEP).toFixed(2) }));
const MOSAIC = +(items.at(-1).t + 1.9).toFixed(2);
const REEL = +(MOSAIC + 3.4).toFixed(2);
const OUTRO = 2.8, TOTAL = +(REEL + OUTRO).toFixed(2);
// Mosaïque : 6 mini palettes en 3×2.
const tiles = ["s1", "s3", "s4", "s5", "s7", "s8"].map((img, i) => ({ img, i, x: 60 + (i % 3) * 330, y: 230 + Math.floor(i / 3) * 520 }));

const imgs = items.map((it) => `      <img id="lk${it.k}" class="lk" src="assets/looks/${it.img}.png" alt="" style="z-index:${it.k + 2}" />`).join("\n");
const names = items.map((it) => `    <div id="nm${it.k}" class="name"><span>${it.name}</span></div>`).join("\n");
const tileHtml = tiles.map((t) => `    <div id="tl${t.i}" class="tile" style="left:${t.x}px;top:${t.y}px"><img src="assets/looks/${t.img}.png" alt="" /></div>`).join("\n");

const tw = [];
tw.push(`tl.fromTo("#card", { y: 40, scale: 0.97 }, { y: 0, scale: 1, duration: 0.6, ease: "expo.out" }, 0);`);
tw.push(`tl.fromTo("#hook2", { opacity: 0, y: 16 }, { opacity: 1, y: 0, duration: 0.35, ease: "expo.out" }, 0.9);`);
tw.push(`tl.to(["#hook1", "#hook2"], { opacity: 0, y: -14, duration: 0.2 }, ${FIRST - 0.15});`);
tw.push(`tl.fromTo("#count", { opacity: 0, y: -10 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, ${FIRST});`);
items.forEach((it, i) => {
  const out = i === items.length - 1 ? MOSAIC - 0.3 : items[i + 1].t - 0.15;
  tw.push(`tl.fromTo("#lk${it.k}", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${it.t});`);
  tw.push(`tl.fromTo("#line", { y: 0, opacity: 1 }, { y: 930, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${it.t});`);
  tw.push(`tl.to("#line", { opacity: 0, duration: 0.03 }, ${+(it.t + W).toFixed(2)});`);
  tw.push(`tl.fromTo("#nm${it.k}", { opacity: 0, y: 24, scale: 0.9 }, { opacity: 1, y: 0, scale: 1, duration: 0.3, ease: "back.out(2)" }, ${+(it.t + 0.12).toFixed(2)});`);
  tw.push(`tl.to("#nm${it.k}", { opacity: 0, y: -16, duration: 0.15 }, ${+out.toFixed(2)});`);
  tw.push(`tl.set("#count", { textContent: "${it.k + 1} / ${items.length}" }, ${it.t});`);
});
// Mosaïque finale : la grande carte s'efface, six mini palettes arrivent en cascade.
tw.push(`tl.to(["#card", "#count"], { scale: 0.85, opacity: 0, duration: 0.35, ease: "power2.in" }, ${+(MOSAIC - 0.3).toFixed(2)});`);
tw.push(`tl.fromTo(".tile", { y: 420, rotation: (i) => (i % 2 ? 6 : -6), opacity: 0 }, { y: 0, rotation: (i) => (i % 2 ? 1.2 : -1.2), opacity: 1, duration: 0.55, ease: "expo.out", stagger: 0.08 }, ${MOSAIC});`);
tw.push(`tl.fromTo("#fin", { opacity: 0, y: -20 }, { opacity: 1, y: 0, duration: 0.35, ease: "expo.out" }, ${+(MOSAIC + 0.35).toFixed(2)});`);
tw.push(`tl.to([".tile", "#fin"], { y: -60, opacity: 0, duration: 0.35, ease: "power2.in" }, ${+(REEL - 0.35).toFixed(2)});`);

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    .hook { position: absolute; left: 40px; right: 40px; text-align: center; font-weight: 900; letter-spacing: -0.04em; color: #111827; }
    #hook1 { top: 40px; font-size: 60px; }
    #hook2 { top: 112px; font-size: 44px; color: #6B7280; }
    #hook1 b, #hook2 b { color: #0F5132; }
    #count { position: absolute; top: 70px; left: 0; right: 0; text-align: center; font-weight: 800; font-size: 34px; color: #6B7280; opacity: 0; }
    #card { position: absolute; left: 190px; top: 190px; width: 700px; height: 930px; border-radius: 28px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 6px #0F5132; }
    .lk, #base { position: absolute; left: 0; top: 0; width: 700px; height: 1353px; }
    .lk { clip-path: inset(0% 0% 100% 0%); }
    #line { position: absolute; left: 0; right: 0; top: 0; height: 8px; background: #0F5132; box-shadow: 0 0 24px #7FD6A6; z-index: 60; opacity: 0; }
    .name { position: absolute; top: 1150px; left: 0; right: 0; text-align: center; opacity: 0; z-index: 70; }
    .name span { display: inline-block; font-weight: 900; font-size: 58px; letter-spacing: -0.03em; color: #fff; background: #0F5132; padding: 12px 34px; border-radius: 999px; }
    .tile { position: absolute; width: 300px; height: 490px; border-radius: 22px; overflow: hidden; background: #fff; box-shadow: 0 24px 60px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; opacity: 0; }
    .tile img { position: absolute; left: 0; top: 0; width: 300px; height: 580px; }
    #fin { position: absolute; top: 70px; left: 0; right: 0; text-align: center; font-weight: 900; font-size: 72px; letter-spacing: -0.04em; color: #111827; opacity: 0; }
    #fin b { color: #0F5132; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1350">
    <div id="hook1" class="hook">Ta palette <b>Propriétés</b></div>
    <div id="hook2" class="hook" style="opacity:0">comme tu ne l'as jamais vue.</div>
    <div id="count">1 / ${items.length}</div>
    <div id="card">
      <img id="base" src="assets/looks/s0.png" alt="" />
${imgs}
      <div id="line"></div>
    </div>
${names}
${tileHtml}
    <div id="fin">Choisis <b>ton style.</b></div>
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    ${tw.join("\n    ")}
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

const sfx = [["whoosh", 0.05], ["pop", 0.9]];
items.forEach((it) => sfx.push(["click", it.t]));
sfx.push(["whoosh", MOSAIC]);
[0, 2, 5].forEach((i) => sfx.push(["pop", +(MOSAIC + 0.08 * i + 0.12).toFixed(2)]));
sfx.push(["whoosh", +(REEL - 0.35).toFixed(2)], ["pop", +(REEL + 0.1).toFixed(2)]);
const vol = { click: 0.8, whoosh: 0.7, pop: 0.45 };
const audio = sfx.map(([n, s], i) =>
  `      <audio id="sx${i}" class="clip" src="assets/sfx/${n}.mp3" data-start="${s}" data-duration="${n === "click" ? 0.37 : 0.6}" data-track-index="${3 + (i % 3)}" data-volume="${vol[n]}"></audio>`).join("\n");

writeFileSync("index.html", `<!doctype html>
<html lang="fr">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1080, height=1350" />
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.15.0/dist/gsap.min.js"></script>
    <style>
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { width: 1080px; height: 1350px; overflow: hidden; background: #FFFFFF; }
      #root { position: relative; width: 100%; height: 100%; font-family: Inter; }
      #bg { position: absolute; inset: 0; background: #FFFFFF; }
      #bg-dots { position: absolute; inset: 0; background-image: radial-gradient(#E5E7EB 2px, transparent 2px); background-size: 40px 40px; opacity: .7; }
    </style>
  </head>
  <body>
    <div id="root" data-composition-id="main" data-start="0" data-duration="${TOTAL}" data-width="1080" data-height="1350">
      <div id="bg"></div>
      <div id="bg-dots"></div>
      <div id="reel" data-composition-id="reel" data-composition-src="compositions/reel.html" data-start="0" data-duration="${REEL}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1350"></div>
      <div id="outro" data-composition-id="outro" data-composition-src="compositions/outro.html" data-start="${REEL}" data-duration="${OUTRO}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1350"></div>
${audio}
    </div>
    <script>
      window.__timelines["main"] = gsap.timeline({ paused: true });
    </script>
  </body>
</html>
`);
console.log(`total ${TOTAL}s`);
