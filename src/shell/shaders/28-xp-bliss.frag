precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float noise(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

float smoothNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = noise(i);
    float b = noise(i + vec2(1.0, 0.0));
    float c = noise(i + vec2(0.0, 1.0));
    float d = noise(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * smoothNoise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = uv;
    p.x *= u_resolution.x / u_resolution.y;

    // Rolling green hills logic
    float hill1 = 0.3 + 0.15 * sin(p.x * 2.0 + u_time * 0.2) + 0.05 * fbm(p * 4.0);
    float hill2 = 0.2 + 0.1 * cos(p.x * 3.5 - u_time * 0.1) + 0.03 * fbm(p * 6.0 + 10.0);
    
    // Sky gradient
    vec3 skyTop = vec3(0.0, 0.45, 0.8);
    vec3 skyBottom = vec3(0.4, 0.7, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Dynamic Clouds
    float cloudDensity = fbm(p * 3.0 + vec2(u_time * 0.05, 0.0));
    vec3 cloudColor = vec3(1.0, 1.0, 1.0);
    color = mix(color, cloudColor, smoothstep(0.5, 0.8, cloudDensity) * 0.6);

    // Hill Colors
    vec3 grassDark = vec3(0.1, 0.4, 0.05);
    vec3 grassLight = vec3(0.3, 0.7, 0.1);
    
    // Far Hill
    if (uv.y < hill1) {
        float shadow = fbm(p * 10.0);
        color = mix(grassDark, grassLight, (uv.y / hill1) * shadow);
        color *= 0.8; // Atmospheric perspective
    }
    
    // Near Hill
    if (uv.y < hill2) {
        float shadow = fbm(p * 8.0 + 50.0);
        color = mix(grassDark * 1.2, grassLight * 1.1, (uv.y / hill2) * shadow);
    }

    // Sun glow
    float sun = 1.0 - distance(uv, vec2(0.8, 0.85));
    color += vec3(1.0, 0.9, 0.6) * pow(max(sun, 0.0), 8.0) * 0.4;

    gl_FragColor = vec4(color, 1.0);
}
