// Animación de la landing: vista lateral de dos compuertas de esclusa escalonadas. El agua (los
// datos) llega turbia y con impurezas por la izquierda; cada compuerta deja pasar menos y más
// limpia, hasta un último tramo bajo, claro y en calma. Se dibuja en WebGL2 con líneas
// horizontales cuyo grosor sigue la "densidad" del agua (como las ondas del logo).
// Se engancha sola a cualquier <canvas data-sluice-flow>, también si Blazor lo vuelve a pintar.
(function () {
    'use strict';

    const MAX_FPS = 30;
    const REDUCED_MOTION_TIME = 12;

    const VERTEX = `#version 300 es
void main() {
  vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;

    const FRAGMENT = `#version 300 es
precision highp float;
uniform vec2 u_size;
uniform float u_dpr;
uniform float u_time;
out vec4 outColor;

const vec3 WATER = vec3(0.298, 0.639, 0.941); // #4CA3F0
const vec3 SILT = vec3(0.427, 0.494, 0.553);  // #6D7E8D: impurezas del agua sin filtrar
const float ROW = 7.0;     // separación entre líneas (px)
const float SEG = 4.0;     // largo de cada tramo de línea (px)
const float GATE_W = 0.016;
const float FLOOR = 0.04;

// Por compuerta (x, y) y por tramo (x, y, z), en fracciones del ancho/alto del canvas.
const vec2 GATE_X = vec2(0.42, 0.72);
const vec2 GATE_TOP = vec2(0.27, 0.19);
const vec3 LEVEL = vec3(0.21, 0.145, 0.10);
const vec3 SPEED = vec3(0.03, 0.07, 0.12);
const vec3 SILT_CUT = vec3(0.6, 0.74, 2.0); // umbral de impurezas: el último tramo no tiene

float hash(vec2 p) {
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 45.32);
  return fract(p.x * p.y);
}

float noise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 w = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), w.x),
             mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), w.x), w.y);
}

float opening(int gate) {
  return gate == 0 ? 0.07 + 0.015 * sin(u_time * 0.25) : 0.055 + 0.012 * sin(u_time * 0.25 + 2.0);
}

int basin(float u) { return u < GATE_X.x ? 0 : (u < GATE_X.y ? 1 : 2); }

// Altura de la superficie del agua (fracción del alto) en la posición horizontal u. Cada tramo
// tiene menos oleaje que el anterior.
float surface(float u) {
  float t = u_time;
  int b = basin(u);
  float waves = 0.010 * sin(u * 14.0 - t * 0.8) + 0.005 * sin(u * 33.0 - t * 1.4);
  float level = LEVEL[b] + waves * (1.0 - 0.4 * float(b));
  if (b > 0) {
    float gate = GATE_X[b - 1];
    level = mix(opening(b - 1), level, smoothstep(gate + 0.03, gate + 0.14, u));
  }
  if (b < 2) {
    level -= 0.03 * smoothstep(GATE_X[b] - 0.15, GATE_X[b], u);
  }
  return level;
}

bool isGate(float u, float v) {
  for (int i = 0; i < 2; i++) {
    if (abs(u - GATE_X[i]) < GATE_W * 0.5 && v > opening(i) && v < GATE_TOP[i]) return true;
  }
  return false;
}

void main() {
  vec2 px = gl_FragCoord.xy / u_dpr;
  vec2 cell = floor(px / vec2(SEG, ROW));
  vec2 center = (cell + 0.5) * vec2(SEG, ROW);
  float u = center.x / u_size.x;
  float v = center.y / u_size.y;

  float density = 0.0;
  float alpha = 0.0;
  float clean = 1.0;
  vec3 color = WATER;

  if (isGate(u, v)) {
    density = 1.0;
    alpha = 1.0;
  } else if (v <= FLOOR) {
    density = 0.05;
    alpha = 0.5;
  } else if (v < surface(u)) {
    int b = basin(u);
    clean = float(b) * 0.5;
    float s = surface(u);
    float depth = (s - v) / max(s - FLOOR, 0.01);
    vec2 q = vec2((u - u_time * SPEED[b]) * 22.0, v * 22.0);
    float n = noise(q) * 0.65 + noise(q * 2.3 + 7.0) * 0.35;
    // La turbulencia se calma tramo a tramo: al final las líneas son casi regulares.
    float flow = mix(n, 0.5, clean * 0.8);
    density = max(mix(0.2, 0.85, flow), 1.0 - smoothstep(0.0, 0.18, depth));
    alpha = mix(1.0, mix(0.35, 0.75, clean), depth);
    color = mix(mix(SILT, WATER, 0.6 + 0.4 * clean), vec3(1.0), 0.15 * step(1.0, clean));

    if (noise(q * 3.1 + 13.0) > SILT_CUT[b]) {
      color = SILT;
      density = mix(0.3, 0.9, hash(cell));
    }

    if (b > 0) {
      float gate = GATE_X[b - 1];
      float foam = (1.0 - smoothstep(gate + 0.01, gate + 0.16, u)) * smoothstep(0.45, 0.8, n);
      foam *= b == 2 ? 0.6 : 1.0;
      color = mix(color, vec3(1.0), foam);
      alpha = max(alpha, foam);
    }
  }

  if (alpha == 0.0) { outColor = vec4(0.0); return; }

  float h = hash(cell);
  float thickness = mix(0.5, ROW * 0.45, density);
  if (h < 0.15) thickness += 0.6 * (1.0 - 0.7 * clean) * sin(u_time * 2.0 + h * 40.0);
  float edge = smoothstep(0.0, 0.08, u) * (1.0 - smoothstep(0.97, 1.0, u));
  float a = clamp(0.5 - (abs(px.y - center.y) - thickness * 0.5) * u_dpr, 0.0, 1.0) * alpha * edge;
  outColor = vec4(color * a, a);
}`;

    function compile(gl) {
        const program = gl.createProgram();
        for (const [type, source] of [[gl.VERTEX_SHADER, VERTEX], [gl.FRAGMENT_SHADER, FRAGMENT]]) {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, source);
            gl.compileShader(shader);
            if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
                console.error(gl.getShaderInfoLog(shader));
                return null;
            }
            gl.attachShader(program, shader);
            gl.deleteShader(shader);
        }
        gl.linkProgram(program);
        return gl.getProgramParameter(program, gl.LINK_STATUS) ? program : null;
    }

    // Canvases ya enganchados, y los que siguen en marcha (para soltarlos al salir del DOM). La
    // guarda no puede ser el atributo: el sincronizado del DOM de Blazor lo devuelve a "".
    const started = new WeakSet();
    const active = new Set();

    function start(canvas) {
        started.add(canvas);
        canvas.dataset.sluiceFlow = 'running'; // solo informativo (lo miran los tests de UI)
        const gl = canvas.getContext('webgl2', { alpha: true, antialias: false, depth: false });
        const program = gl && compile(gl);
        if (!program) {
            return;
        }

        const size = gl.getUniformLocation(program, 'u_size');
        const dpr = gl.getUniformLocation(program, 'u_dpr');
        const time = gl.getUniformLocation(program, 'u_time');
        const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const startedAt = performance.now();
        // Se dibuja solo si el canvas está en pantalla y la pestaña visible: el primer aviso del
        // IntersectionObserver llega justo tras observe().
        let intersecting = false;
        let visible = false;
        let raf = 0;
        let lastDraw = 0;

        function draw(now) {
            const ratio = Math.min(window.devicePixelRatio || 1, 2);
            const width = canvas.clientWidth;
            const height = canvas.clientHeight;
            const pw = Math.round(width * ratio);
            const ph = Math.round(height * ratio);
            if (canvas.width !== pw || canvas.height !== ph) {
                canvas.width = pw;
                canvas.height = ph;
            }
            gl.viewport(0, 0, pw, ph);
            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT);
            gl.useProgram(program);
            gl.uniform2f(size, width, height);
            gl.uniform1f(dpr, ratio);
            gl.uniform1f(time, reducedMotion ? REDUCED_MOTION_TIME : (now - startedAt) / 1000);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }

        function loop(now) {
            raf = 0;
            if (!visible || !canvas.isConnected) {
                return;
            }
            if (now - lastDraw >= 1000 / MAX_FPS - 2) {
                lastDraw = now;
                draw(now);
            }
            raf = requestAnimationFrame(loop);
        }

        function resume() {
            if (reducedMotion) {
                draw(performance.now());
            } else if (visible && raf === 0) {
                raf = requestAnimationFrame(loop);
            }
        }

        function updateVisibility() {
            visible = intersecting && document.visibilityState === 'visible';
            resume();
        }

        const intersection = new IntersectionObserver(function (entries) {
            intersecting = entries[entries.length - 1].isIntersecting;
            updateVisibility();
        });
        intersection.observe(canvas);
        document.addEventListener('visibilitychange', updateVisibility);
        const resize = new ResizeObserver(function () {
            if (reducedMotion) {
                resume();
            }
        });
        resize.observe(canvas);

        // Fuera del DOM (p. ej. tras una navegación de Blazor) se suelta todo, también el
        // contexto WebGL: el navegador solo admite unos pocos a la vez.
        const flow = {
            canvas: canvas,
            stop: function () {
                active.delete(flow);
                cancelAnimationFrame(raf);
                raf = 0;
                intersection.disconnect();
                resize.disconnect();
                document.removeEventListener('visibilitychange', updateVisibility);
                const lose = gl.getExtension('WEBGL_lose_context');
                if (lose) {
                    lose.loseContext();
                }
            },
        };
        active.add(flow);
        resume();
    }

    function scan() {
        active.forEach(function (flow) {
            if (!flow.canvas.isConnected) {
                flow.stop();
            }
        });
        document.querySelectorAll('canvas[data-sluice-flow]').forEach(function (canvas) {
            if (!started.has(canvas)) {
                start(canvas);
            }
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', scan);
    } else {
        scan();
    }
    new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
})();
