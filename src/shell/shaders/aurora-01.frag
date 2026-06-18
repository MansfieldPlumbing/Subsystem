// aurora-01 — northern lights over a starlit ridge. GLSL ES 1.00, the shell/shaders convention:
// u_resolution/u_time required, u_camera optional (launcher scroll/zoom parallax via Wp).
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
        p = p * 2.1 + vec2(13.7, 7.3);
        a *= 0.5;
    }
    return v;
}

float stars(vec2 uv, float density, float t) {
    vec2 g = floor(uv * density);
    vec2 f = fract(uv * density);
    float h = hash(g);
    if (h < 0.985) return 0.0;
    vec2 pos = vec2(hash(g + 1.3), hash(g + 2.7)) * 0.6 + 0.2;
    float d = length(f - pos);
    float tw = 0.55 + 0.45 * sin(t * (0.6 + h * 2.4) + h * 40.0);
    return smoothstep(0.10, 0.0, d) * tw;
}

void main() {
    vec2 st = gl_FragCoord.xy / u_resolution.xy;
    st += u_camera * 0.00006;                       // the sky drifts a fraction behind the cards
    vec2 a = st;
    a.x *= u_resolution.x / u_resolution.y;
    float t = u_time;

    // polar night gradient
    vec3 col = mix(vec3(0.012, 0.02, 0.055), vec3(0.035, 0.06, 0.13), st.y);

    // two parallax star layers (far layer drifts slower)
    col += vec3(0.9, 0.95, 1.0) * stars(a + u_camera * 0.00002, 26.0, t) * 0.8;
    col += vec3(0.8, 0.9, 1.0) * stars(a * 1.7 + 4.2 + u_camera * 0.00004, 34.0, t) * 0.5;

    // three stacked aurora curtains: a noise-wobbled gaussian band, combed into vertical rays,
    // ramped green (base) -> teal -> violet (crown)
    for (int i = 0; i < 3; i++) {
        float fi = float(i);
        float wob = fbm(vec2(a.x * 1.4 + fi * 3.1, t * 0.035 + fi * 11.0));
        float cy = 0.52 + 0.14 * fi + 0.16 * (wob - 0.5);
        float band = exp(-pow((st.y - cy) * (5.5 - fi), 2.0));
        float rays = 0.55 + 0.45 * sin(a.x * 28.0 + wob * 9.0 + t * 0.25 + fi * 2.1);
        float glow = band * rays * (0.55 - fi * 0.13);
        float h = clamp((st.y - cy) * 3.0 + 0.5, 0.0, 1.0);
        vec3 ac = mix(vec3(0.10, 0.85, 0.45), vec3(0.45, 0.25, 0.90), h);
        col += ac * glow;
    }

    // ridge silhouette
    float ridge = 0.14 + 0.06 * fbm(vec2(a.x * 2.2, 3.7));
    float ground = smoothstep(ridge + 0.004, ridge - 0.004, st.y);
    col = mix(col, vec3(0.008, 0.012, 0.028), ground);

    gl_FragColor = vec4(col, 1.0);
}
