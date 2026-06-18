precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(float n) {
    return fract(sin(n) * 43758.5453123);
}

float noise(vec2 x) {
    vec2 p = floor(x);
    vec2 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    float n = p.x + p.y * 57.0;
    return mix(mix(hash(n + 0.0), hash(n + 1.0), f.x),
               mix(hash(n + 57.0), hash(n + 58.0), f.x), f.y);
}

float fbm(vec2 p) {
    float f = 0.0;
    f += 0.5000 * noise(p); p *= 2.02;
    f += 0.2500 * noise(p); p *= 2.03;
    f += 0.1250 * noise(p); p *= 2.01;
    f += 0.0625 * noise(p);
    return f;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Sky Background
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.6, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Sun Glow
    float sun = 1.0 - distance(uv, vec2(0.8, 0.85));
    color += vec3(1.0, 0.9, 0.6) * pow(max(sun, 0.0), 8.0) * 0.5;

    // Clouds
    float cloudNoise = fbm(vec2(p.x * 1.5 + u_time * 0.05, p.y * 4.0));
    float cloudMask = smoothstep(0.4, 0.7, cloudNoise);
    color = mix(color, vec3(1.0), cloudMask * 0.4 * smoothstep(0.2, 0.5, uv.y));

    // Distant Hills
    float h1 = 0.35 + 0.12 * sin(p.x * 0.8 + u_time * 0.1) + 0.05 * noise(vec2(p.x * 2.0, u_time * 0.05));
    vec3 hillColor1 = vec3(0.2, 0.5, 0.2);
    if (uv.y < h1) {
        float shadow = smoothstep(h1, h1 - 0.1, uv.y);
        color = mix(hillColor1 * 0.8, hillColor1, uv.y / h1);
        color *= 0.8 + 0.2 * noise(p * 10.0);
    }

    // Mid Hills
    float h2 = 0.25 + 0.15 * sin(p.x * 1.2 - u_time * 0.2 + 2.0) + 0.08 * noise(vec2(p.x * 1.5, u_time * 0.1));
    vec3 hillColor2 = vec3(0.3, 0.6, 0.1);
    if (uv.y < h2) {
        color = mix(hillColor2 * 0.7, hillColor2, uv.y / h2);
        color *= 0.9 + 0.1 * noise(p * 15.0);
        // Highlight on top
        color += vec3(0.1, 0.2, 0.0) * smoothstep(h2 - 0.02, h2, uv.y);
    }

    // Foreground Hills
    float h3 = 0.15 + 0.1 * sin(p.x * 0.5 + u_time * 0.3 + 4.0) + 0.05 * sin(p.x * 3.0);
    vec3 hillColor3 = vec3(0.4, 0.7, 0.2);
    if (uv.y < h3) {
        color = mix(hillColor3 * 0.6, hillColor3, uv.y / h3);
        color *= 0.95 + 0.05 * noise(p * 20.0);
        color += vec3(0.1, 0.2, 0.1) * smoothstep(h3 - 0.03, h3, uv.y);
    }

    // Gentle Vignette
    color *= 1.0 - 0.2 * length(uv - 0.5);

    gl_FragColor = vec4(color, 1.0);
}
