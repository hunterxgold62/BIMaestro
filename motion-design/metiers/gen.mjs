// Vidéo 3 « Un look par métier » : chaque métier a son arborescence.
// Entre deux métiers, un rideau vert monte avec le nom du métier, puis dévoile la nouvelle arborescence.
// node gen.mjs → index.html + compositions/reel.html
import { writeFileSync } from "node:fs";

const HOOK = 2.2, CH = 2.6, CURTAIN = 0.85;
// [métier, n° de variante dans le rush, sous-titre]
const jobs = [
  ["Architecte", 1, "Plan d'architecte · icônes archi"],
  ["Structure", 2, "Grille · gras orange"],
  ["Plomberie & CVC", 3, "Encre dans l'eau · dégradés bleus"],
  ["Électricité", 4, "Constellation · texte jaune"],
  ["BIM manager", 5, "Verre dépoli · badges"],
];
const items = jobs.map(([name, vk, sub], k) => ({ k, name, vk, sub, t: +(k * CH).toFixed(2) }));
const REEL = +(items.length * CH + 0.3).toFixed(2);
const OUTRO = 2.8, TOTAL = +(HOOK + REEL + OUTRO).toFixed(2);

const vids = items.map((it) => {
  const start = it.t + CURTAIN * 0.5;
  const end = it.k === items.length - 1 ? REEL : items[it.k + 1].t + CURTAIN * 0.5;
  return `      <video id="mv${it.k}" class="clip mv" src="assets/metiers.mp4" data-start="${+start.toFixed(2)}" data-duration="${+(end - start).toFixed(2)}" data-media-start="${+(1.1 + 5 * it.vk + 1.5).toFixed(2)}" muted playsinline style="z-index:${it.k + 1}"></video>`;
}).join("\n");
const names = items.map((it) => `    <div id="mn${it.k}" class="mname">${it.name}</div>`).join("\n");
const labels = items.map((it) =>
  `    <div id="ml${it.k}" class="mlab"><span>${String(it.k + 1).padStart(2, "0")} / ${String(items.length).padStart(2, "0")}</span><b>${it.name}</b><i>${it.sub}</i></div>`).join("\n");

const tw = [`tl.set("#curtain", { yPercent: 100 }, 0);`, `tl.set(".mname", { opacity: 0 }, 0);`];
items.forEach((it) => {
  const t = it.t;
  // rideau : monte, affiche le nom, puis repart vers le haut
  tw.push(`tl.fromTo("#curtain", { yPercent: 100 }, { yPercent: 0, duration: 0.3, ease: "power3.inOut", immediateRender: false }, ${t});`);
  tw.push(`tl.fromTo("#mn${it.k}", { opacity: 0, y: 60 }, { opacity: 1, y: 0, duration: 0.25, ease: "expo.out" }, ${+(t + 0.15).toFixed(2)});`);
  tw.push(`tl.to("#mn${it.k}", { opacity: 0, y: -60, duration: 0.2, ease: "power2.in" }, ${+(t + CURTAIN - 0.25).toFixed(2)});`);
  tw.push(`tl.to("#curtain", { yPercent: -100, duration: 0.3, ease: "power3.inOut" }, ${+(t + CURTAIN - 0.3).toFixed(2)});`);
  tw.push(`tl.set("#curtain", { yPercent: 100 }, ${+(t + CURTAIN + 0.01).toFixed(2)});`);
  tw.push(`tl.fromTo("#ml${it.k}", { opacity: 0, x: -40 }, { opacity: 1, x: 0, duration: 0.3, ease: "expo.out" }, ${+(t + CURTAIN).toFixed(2)});`);
  if (it.k < items.length - 1) tw.push(`tl.to("#ml${it.k}", { opacity: 0, duration: 0.1 }, ${+(items[it.k + 1].t).toFixed(2)});`);
  tw.push(`tl.fromTo("#mv${it.k}", { scale: 1.06 }, { scale: 1, duration: 1.6, ease: "power2.out" }, ${+(t + CURTAIN * 0.5).toFixed(2)});`);
});
tw.push(`tl.to(["#mcard", "#ml${items.length - 1}"], { y: -60, opacity: 0, duration: 0.3, ease: "power2.in" }, ${+(REEL - 0.3).toFixed(2)});`);

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; overflow: hidden; }
    #mcard { position: absolute; left: 330px; top: 300px; width: 620px; height: 960px; border-radius: 28px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
    .mv { position: absolute; left: 0; top: 0; width: 620px; height: 1469px; object-fit: fill; transform-origin: 0 0; }
    #mbar { position: absolute; left: 0; top: 0; bottom: 0; width: 10px; background: #0F5132; z-index: 51; }
    .mlab { position: absolute; left: 0; width: 330px; top: 560px; padding: 0 22px 0 50px; opacity: 0; display: flex; flex-direction: column; gap: 10px; }
    .mlab span { align-self: flex-start; color: #fff; background: #0F5132; font-weight: 900; font-size: 28px; padding: 6px 14px; border-radius: 999px; }
    .mlab b { color: #111827; font-weight: 900; font-size: 50px; letter-spacing: -0.03em; line-height: 1.02; }
    .mlab i { font-style: normal; color: #6B7280; font-weight: 700; font-size: 26px; line-height: 1.25; }
    #curtain { position: absolute; inset: 0; background: #0F5132; z-index: 80; }
    .mname { position: absolute; left: 80px; right: 140px; top: 760px; text-align: center; color: #fff; font-weight: 900; font-size: 104px; letter-spacing: -0.04em; line-height: 1; z-index: 81; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1920">
    <div id="mcard">
${vids}
      <div id="mbar"></div>
    </div>
${labels}
    <div id="curtain"></div>
${names}
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    ${tw.join("\n    ")}
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

const hook = `<template>
  <style>
    #root { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; padding: 0 180px 320px 120px; }
    .hk { font-weight: 900; font-size: 96px; line-height: 1; letter-spacing: -0.05em; color: #111827; text-align: center; }
    #hk2 { position: relative; color: #0F5132; }
    #hkm { position: absolute; left: -10px; right: -10px; bottom: 4px; height: 28px; background: #7FD6A6; border-radius: 8px; z-index: -1; transform-origin: 0 50%; }
  </style>
  <div id="root" data-composition-id="hook" data-width="1080" data-height="1920">
    <div id="hk1" class="hk">Chaque métier,</div>
    <div id="hk2" class="hk"><span>son arborescence.</span><div id="hkm"></div></div>
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    tl.fromTo("#hk1", { y: 30 }, { y: 0, duration: 0.45, ease: "expo.out" }, 0);
    tl.fromTo("#hk2", { y: 40, opacity: 0 }, { y: 0, opacity: 1, duration: 0.45, ease: "expo.out" }, 0.3);
    tl.fromTo("#hkm", { scaleX: 0 }, { scaleX: 1, duration: 0.45, ease: "power3.inOut" }, 0.55);
    tl.to("#root", { y: -200, opacity: 0, duration: 0.35, ease: "power2.in" }, 1.85);
    window.__timelines["hook"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/hook.html", hook);

const sfx = [["pop", 0.5]];
items.forEach((it) => sfx.push(["whoosh", +(HOOK + it.t).toFixed(2)], ["pop", +(HOOK + it.t + 0.15).toFixed(2)]));
sfx.push(["whoosh", +(HOOK + REEL - 0.3).toFixed(2)], ["pop", +(HOOK + REEL + 0.1).toFixed(2)]);
const audio = sfx.map(([n, s], i) =>
  `      <audio id="sx${i}" class="clip" src="assets/sfx/${n}.mp3" data-start="${s}" data-duration="0.6" data-track-index="${3 + (i % 2)}" data-volume="${n === "pop" ? 0.5 : 0.75}"></audio>`).join("\n");

writeFileSync("index.html", `<!doctype html>
<html lang="fr">
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
`);
console.log(`total ${TOTAL}s`);
