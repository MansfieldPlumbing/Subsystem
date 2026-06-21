// aurora-02 — moonlit aurora, tinted by a theme accent when an engine feeds it. GLSL ES 1.00;
// u_resolution/u_time required, u_camera optional (parallax), u_accent optional (0..1 RGB — a
// zero vector means "engine doesn't feed it", so the shader grounds to its own teal rather than
// going black; the native Wp engine currently doesn't feed it).
precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;
uniform vec2 u_camera;
uniform vec3 u_accent;

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
        p = p * 2.1 + vec2(5.2, 9.1);
        a *= 0.5;
    }
    return v;
}

float stars(vec2 uv, float density, float t) {
    vec2 g = floor(uv * density);
    vec2 f = fract(uv * density);
    float h = hash(g);
    if (h < 0.987) return 0.0;
    vec2 pos = vec2(hash(g + 1.3), hash(g + 2.7)) * 0.6 + 0.2;
    float d = length(f - pos);
    float tw = 0.55 + 0.45 * sin(t * (0.5 + h * 2.0) + h * 40.0);
    return smoothstep(0.09, 0.0, d) * tw;
}

void main() {
    vec2 st = gl_FragCoord.xy / u_resolution.xy;
    st += u_camera * 0.00006;
    vec2 a = st;
    a.x *= u_resolution.x / u_resolution.y;
    float t = u_time;

    // the theme accent tints the aurora; grounded teal when the engine doesn't feed the uniform
    vec3 acc = (dot(u_accent, vec3(1.0)) < 0.001) ? vec3(0.30, 0.76, 1.0) : u_accent;

    vec3 col = mix(vec3(0.015, 0.018, 0.05), vec3(0.05, 0.055, 0.12), st.y);
    col += vec3(0.9, 0.95, 1.0) * stars(a, 30.0, t) * 0.7;

    // the moon: a soft disc + wide glow, high right
    vec2 mpos = vec2(u_resolution.x / u_resolution.y * 0.78, 0.80);
    float md = length(a - mpos);
    col += vec3(0.95, 0.97, 1.0) * smoothstep(0.055, 0.045, md);          // disc
    col += vec3(0.55, 0.6, 0.75) * exp(-md * 9.0) * 0.35;                 // halo

    // two broad accent-tinted curtains, slower and wider than aurora-01
    for (int i = 0; i < 2; i++) {
        float fi = float(i);
        float wob = fbm(vec2(a.x * 1.1 + fi * 7.7, t * 0.028 + fi * 3.0));
        float cy = 0.50 + 0.18 * fi + 0.18 * (wob - 0.5);
        float band = exp(-pow((st.y - cy) * (4.2 - fi), 2.0));
        float rays = 0.6 + 0.4 * sin(a.x * 18.0 + wob * 7.0 + t * 0.2 + fi * 1.7);
        float h = clamp((st.y - cy) * 2.6 + 0.5, 0.0, 1.0);
        vec3 ac = mix(mix(vec3(0.10, 0.85, 0.45), acc, 0.45), acc, h);
        col += ac * band * rays * (0.5 - fi * 0.15);
    }

    // water: mirror-ish shimmer below the horizon
    float horizon = 0.18;
    if (st.y < horizon) {
        float ry = horizon + (horizon - st.y);
        float shim = fbm(vec2(a.x * 6.0, ry * 14.0 - t * 0.12));
        vec3 refl = mix(vec3(0.02, 0.03, 0.06), acc * 0.25, exp(-(horizon - st.y) * 9.0) * shim);
        col = mix(col, refl, 0.85);
    }

    gl_FragColor = vec4(col, 1.0);
}
