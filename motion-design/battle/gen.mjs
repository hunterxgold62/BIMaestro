// Vidéo 1 « Battle » : Revit figé à gauche, Revit + BIMaestro qui change de look à droite.
// node gen.mjs  → écrit index.html et compositions/reel.html
import { writeFileSync } from "node:fs";

const STEP = 1.5, W = 0.3, FIRST = 1.6;
// [rush, n° de variante]
const looks = [["icones", 1], ["effets", 8], ["effets", 7], ["icones", 3], ["effets", 9], ["effets", 6],
  ["effets", 13], ["icones", 4], ["icones", 5]];
const items = looks.map(([rush, vk], k) => {
  const reveal = +(FIRST + k * STEP).toFixed(2);
  const end = k === looks.length - 1 ? reveal + W + 2.9 : reveal + STEP + W;
  return { k, rush, reveal, len: +(end - reveal).toFixed(2), media: +(1.1 + 5 * vk + 1.3).toFixed(2) };
});
const SPLIT = +(items.at(-1).reveal + W + 0.9).toFixed(2); // le côté Revit bascule aussi
const REEL = +(SPLIT + 1.9).toFixed(2);
const OUTRO = 2.8, TOTAL = +(REEL + OUTRO).toFixed(2);

const vids = items.map((it) =>
  `      <video id="bv${it.k}" class="clip bv" src="assets/${it.rush}.mp4" data-start="${it.reveal}" data-duration="${it.len}" data-media-start="${it.media}" muted playsinline style="z-index:${it.k + 2}"></video>`).join("\n");
const tw = items.map((it) => [
  `tl.fromTo("#bv${it.k}", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${it.reveal});`,
  `tl.fromTo("#bline", { y: 0, opacity: 1 }, { y: 900, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${it.reveal});`,
  `tl.to("#bline", { opacity: 0, duration: 0.03 }, ${+(it.reveal + W).toFixed(2)});`,
].join("\n    ")).join("\n    ");

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    .bcard { position: absolute; top: 330px; width: 460px; height: 900px; border-radius: 26px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
    #bl { left: 60px; }
    #br { left: 560px; box-shadow: 0 30px 80px rgba(17,24,39,.22), 0 0 0 6px #0F5132; }
    .bimg, .bv { position: absolute; left: 0; top: 0; width: 460px; height: 1090px; object-fit: fill; }
    #bl-after { clip-path: inset(0% 100% 0% 0%); z-index: 3; }
    #bline { position: absolute; left: 0; right: 0; top: 0; height: 7px; background: #0F5132; box-shadow: 0 0 22px #7FD6A6; z-index: 60; opacity: 0; }
    #bvline { position: absolute; top: 0; bottom: 0; left: 0; width: 7px; background: #0F5132; box-shadow: 0 0 22px #7FD6A6; z-index: 60; opacity: 0; }
    .bpill { position: absolute; top: 250px; width: 460px; text-align: center; }
    .bpill span { display: inline-block; font-weight: 900; font-size: 36px; padding: 10px 24px; border-radius: 999px; }
    #bpl { left: 60px; } #bpl span { background: #E5E7EB; color: #374151; }
    #bpr { left: 560px; } #bpr span { background: #0F5132; color: #fff; }
    #bvs { position: absolute; left: 490px; top: 720px; width: 100px; height: 100px; border-radius: 50%; background: #111827; color: #fff; font-weight: 900; font-size: 40px; display: flex; align-items: center; justify-content: center; z-index: 70; }
    .bsub { position: absolute; top: 1250px; left: 60px; right: 60px; text-align: center; font-weight: 900; font-size: 64px; letter-spacing: -0.03em; color: #111827; opacity: 0; }
    .bsub b { color: #0F5132; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1920">
    <div id="bpl" class="bpill"><span>Revit</span></div>
    <div id="bpr" class="bpill"><span>Revit + BIMaestro</span></div>
    <div id="bl" class="bcard">
      <img class="bimg" src="assets/base.png" alt="" />
      <img id="bl-after" class="bimg" src="assets/nuit.png" alt="" />
      <div id="bvline"></div>
    </div>
    <div id="br" class="bcard">
      <img class="bimg" src="assets/base.png" alt="" style="z-index:1" />
${vids}
      <div id="bline"></div>
    </div>
    <div id="bvs">VS</div>
    <div id="bs1" class="bsub">Même projet.</div>
    <div id="bs2" class="bsub"><b>9 looks</b> qui bougent.</div>
    <div id="bs3" class="bsub">Et toi, <b>tu choisis lequel ?</b></div>
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    tl.fromTo("#bl", { x: -600 }, { x: 0, duration: 0.5, ease: "expo.out" }, 0);
    tl.fromTo("#br", { x: 600 }, { x: 0, duration: 0.5, ease: "expo.out" }, 0.08);
    tl.fromTo(["#bpl", "#bpr"], { opacity: 0, y: -20 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out", stagger: 0.1 }, 0.3);
    tl.fromTo("#bvs", { scale: 0, rotation: -30 }, { scale: 1, rotation: 0, duration: 0.4, ease: "back.out(2.5)" }, 0.55);
    tl.to("#bvs", { scale: 0, duration: 0.2, ease: "power2.in" }, ${FIRST - 0.1});
    tl.fromTo("#bs1", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, 0.7);
    tl.to("#bs1", { opacity: 0, y: -20, duration: 0.2 }, 4.2);
    tl.fromTo("#bs2", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, 4.4);
    tl.to("#bs2", { opacity: 0, y: -20, duration: 0.2 }, ${+(SPLIT - 0.3).toFixed(2)});
    ${tw}
    // finale : le côté Revit bascule aussi, balayage horizontal
    tl.to("#bpl span", { backgroundColor: "#0F5132", color: "#ffffff", duration: 0.3 }, ${SPLIT});
    tl.to("#bl-after", { clipPath: "inset(0% 0% 0% 0%)", duration: 0.5, ease: "power3.inOut" }, ${SPLIT});
    tl.fromTo("#bvline", { x: 0, opacity: 1 }, { x: 460, opacity: 1, duration: 0.5, ease: "power3.inOut" }, ${SPLIT});
    tl.to("#bvline", { opacity: 0, duration: 0.03 }, ${+(SPLIT + 0.5).toFixed(2)});
    tl.fromTo("#bs3", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, ${+(SPLIT + 0.2).toFixed(2)});
    tl.to(["#bl", "#br", "#bpl", "#bpr", "#bs3"], { y: -80, opacity: 0, duration: 0.35, ease: "power2.in" }, ${+(REEL - 0.35).toFixed(2)});
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

const sfx = [["whoosh", 0.05], ["pop", 0.55]];
items.forEach((it) => sfx.push(["click", it.reveal]));
sfx.push(["whoosh", SPLIT], ["whoosh", +(REEL - 0.35).toFixed(2)], ["pop", +(REEL + 0.1).toFixed(2)]);
const audio = sfx.map(([n, s], i) =>
  `      <audio id="sx${i}" class="clip" src="assets/sfx/${n}.mp3" data-start="${s}" data-duration="${n === "click" ? 0.37 : 0.6}" data-track-index="${3 + (i % 2)}" data-volume="${n === "click" ? 0.6 : 0.8}"></audio>`).join("\n");

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
      <div id="reel" data-composition-id="reel" data-composition-src="compositions/reel.html" data-start="0" data-duration="${REEL}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
      <div id="outro" data-composition-id="outro" data-composition-src="compositions/outro.html" data-start="${REEL}" data-duration="${OUTRO}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
${audio}
    </div>
    <script>
      window.__timelines["main"] = gsap.timeline({ paused: true });
    </script>
  </body>
</html>
`);
console.log(`total ${TOTAL}s`);
