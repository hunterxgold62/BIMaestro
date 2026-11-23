// Capture d'écran pour les vidéos motion design BIMaestro.
// Lance ffmpeg directement (shell: false, windowsHide: true) — voir AGENTS.md.
//
//   node capture.mjs still                       -> capture plein écran (pour caler le cadrage)
//   node capture.mjs record <nom> [secondes]     -> filme la zone définie dans zone.json
//   node capture.mjs record <nom> full [secondes]-> filme tout l'écran
//
// zone.json : { "x": 1960, "y": 220, "w": 600, "h": 1125 }  (pixels écran)
// Arrêt d'un enregistrement sans durée : touche q dans la console, ou Ctrl+C.

import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "rushes");
if (!existsSync(out)) mkdirSync(out, { recursive: true });

const [mode, name = "prise", a, b] = process.argv.slice(2);
const even = (n) => Math.floor(n / 2) * 2;

function run(args) {
  const p = spawn("ffmpeg", args, { shell: false, windowsHide: true, stdio: "inherit" });
  p.on("exit", (code) => process.exit(code ?? 0));
}

if (mode === "still") {
  run(["-v", "error", "-y", "-f", "gdigrab", "-i", "desktop", "-frames:v", "1", join(out, "ecran.png")]);
} else if (mode === "record") {
  const full = a === "full";
  const secs = full ? b : a;
  const args = ["-v", "warning", "-y", "-f", "gdigrab", "-framerate", "60", "-draw_mouse", "1"];
  if (!full) {
    const z = JSON.parse(readFileSync(join(here, "zone.json"), "utf8"));
    args.push("-offset_x", String(z.x), "-offset_y", String(z.y), "-video_size", `${even(z.w)}x${even(z.h)}`);
  }
  if (secs) args.push("-t", String(secs));
  args.push("-i", "desktop", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-pix_fmt", "yuv420p", join(out, `${name}.mp4`));
  console.log(`Enregistrement -> rushes/${name}.mp4  (q pour arrêter)`);
  run(args);
} else {
  console.log("Usage : node capture.mjs still | record <nom> [secondes] | record <nom> full [secondes]");
}
