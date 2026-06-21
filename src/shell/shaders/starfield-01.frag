// starfield-01 — deep space: parallax star layers, a milky-way band, the occasional meteor.
// GLSL ES 1.00; u_resolution/u_time required, u_camera optional (parallax).
precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;
uniform vec2 u_camera;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++) {
        v += a * noise(p);
        p = p * 2.0 + vec2(11.3, 17.9);
        a *= 0.5;
    }
    return v;
}

float stars(vec2 uv, float density, float thresh, float t) {
    vec2 g = floor(uv * density);
    vec2 f = fract(uv * density);
    float h = hash(g);
    if (h < thresh) return 0.0;
    vec2 pos = vec2(hash(g + 1.3), hash(g + 2.7)) * 0.6 + 0.2;
    float d = length(f - pos);
    float tw = 0.6 + 0.4 * sin(t * (0.4 + h * 2.6) + h * 50.0);
    return smoothstep(0.12, 0.0, d) * tw;
}

void main() {
    vec2 st = gl_FragCoord.xy / u_resolution.xy;
    vec2 a = st;
    a.x *= u_resolution.x / u_resolution.y;
    float t = u_time;

    vec3 col = mix(vec3(0.004, 0.006, 0.016), vec3(0.012, 0.014, 0.04), st.y);

    // milky way: a diagonal fbm dust band
    vec2 r = a - vec2(0.7, 0.5);
    r = mat2(0.866, -0.5, 0.5, 0.866) * r;          // ~30° tilt
    float dust = fbm(r * vec2(2.0, 5.0) + vec2(t * 0.004, 0.0));
    float band = exp(-pow(r.y * 2.4, 2.0));
    col += vec3(0.16, 0.17, 0.26) * band * dust;
    col += vec3(0.30, 0.25, 0.38) * band * smoothstep(0.55, 0.9, dust) * 0.6;

    // three parallax star layers — deeper layers drift slower behind the camera
    col += vec3(1.0) * stars(a + u_camera * 0.00002 + vec2(t * 0.0008, 0.0), 22.0, 0.984, t) * 0.95;
    col += vec3(0.85, 0.9, 1.0) * stars(a * 1.6 + 3.1 + u_camera * 0.00004 + vec2(t * 0.0014, 0.0), 30.0, 0.986, t) * 0.6;
    col += vec3(0.7, 0.78, 1.0) * stars(a * 2.5 + 7.9 + u_camera * 0.00007 + vec2(t * 0.0022, 0.0), 40.0, 0.988, t) * 0.35;

    // a meteor every ~9s: a bright head with an exponential tail along its track
    float cycle = 9.0;
    float k = floor(t / cycle);
    float p = fract(t / cycle);
    vec2 m0 = vec2(hash(vec2(k, 1.0)) * 1.4 - 0.2, 0.75 + 0.25 * hash(vec2(k, 7.0)));
    vec2 dir = normalize(vec2(0.72, -0.55));
    vec2 head = m0 + dir * p * 1.5;
    vec2 v = a - head;
    float s = clamp(-dot(v, dir), 0.0, 0.22);
    float md = length(v + dir * s);
    float fade = smoothstep(0.0, 0.08, p) * smoothstep(1.0, 0.62, p) * (1.0 - s / 0.25);
    col += vec3(0.9, 0.95, 1.0) * exp(-md * 90.0) * fade;

    gl_FragColor = vec4(col, 1.0);
}
