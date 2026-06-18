precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

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
    vec2 shift = vec2(100.0);
    mat2 rot = mat2(cos(0.5), sin(0.5), -sin(0.5), cos(0.5));
    for (int i = 0; i < 6; ++i) {
        v += a * noise(p);
        p = rot * p * 2.0 + shift;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    uv.x *= u_resolution.x / u_resolution.y;

    float t = u_time * 0.05;
    
    vec2 q = vec2(0.0);
    q.x = fbm(uv + 0.1 * t);
    q.y = fbm(uv + vec2(1.0));

    vec2 r = vec2(0.0);
    r.x = fbm(uv + 1.0 * q + vec2(1.7, 9.2) + 0.15 * t);
    r.y = fbm(uv + 1.0 * q + vec2(8.3, 2.8) + 0.126 * t);

    float f = fbm(uv + r);

    vec3 skyColor = vec3(0.3, 0.5, 0.85);
    vec3 cloudColor = vec3(1.0, 1.0, 1.0);
    vec3 darkCloud = vec3(0.6, 0.65, 0.7);

    vec3 color = mix(skyColor, darkCloud, clamp(f * f * 4.0, 0.0, 1.0));
    color = mix(color, cloudColor, clamp(pow(f, 3.0) * 3.0, 0.0, 1.0));
    
    // Add soft sun glow
    float sun = 0.5 - distance(uv, vec2(1.0, 0.8));
    color += 0.2 * vec3(1.0, 0.8, 0.5) * max(0.0, sun);

    // Vignette
    color *= 0.8 + 0.2 * sin(uv.y * 3.14159);
    
    gl_FragColor = vec4(color, 1.0);
}
