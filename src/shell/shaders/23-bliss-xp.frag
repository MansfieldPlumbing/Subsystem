precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), f.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * noise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Sky gradient
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.5, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Clouds
    vec2 cloudUV = p * 2.0 + vec2(u_time * 0.05, 0.0);
    float n = fbm(cloudUV);
    float cloudMask = smoothstep(0.4, 0.7, n);
    color = mix(color, vec3(1.0), cloudMask * 0.6 * uv.y);

    // Hill 1 (Back)
    float h1 = 0.4 + 0.15 * sin(p.x * 1.2 + 2.0) + 0.05 * cos(p.x * 2.5 + u_time * 0.1);
    vec3 green1 = vec3(0.2, 0.6, 0.1);
    if (uv.y < h1) {
        float shadow = smoothstep(h1, h1 - 0.1, uv.y);
        color = mix(green1 * 0.8, green1, shadow);
    }

    // Hill 2 (Middle)
    float h2 = 0.3 + 0.12 * cos(p.x * 0.8 + 4.5) + 0.04 * sin(p.x * 3.5 - u_time * 0.05);
    vec3 green2 = vec3(0.3, 0.7, 0.15);
    if (uv.y < h2) {
        float shadow = smoothstep(h2, h2 - 0.15, uv.y);
        color = mix(green2 * 0.8, green2, shadow);
    }

    // Hill 3 (Front)
    float h3 = 0.15 + 0.18 * sin(p.x * 0.6 + 0.5) + 0.03 * cos(p.x * 5.0 + u_time * 0.02);
    vec3 green3 = vec3(0.4, 0.8, 0.2);
    if (uv.y < h3) {
        float highlight = smoothstep(h3 - 0.05, h3, uv.y);
        color = mix(green3 * 0.7, green3 * 1.1, highlight);
    }

    // Sun glow
    float sun = 1.0 - distance(uv, vec2(0.8, 0.85));
    color += vec3(1.0, 0.9, 0.6) * pow(max(0.0, sun), 8.0) * 0.4;

    // Subtle grass texture
    if (uv.y < max(h1, max(h2, h3))) {
        float grass = noise(p * 80.0 + u_time * 0.1);
        color += (grass - 0.5) * 0.05;
    }

    gl_FragColor = vec4(color, 1.0);
}
