precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
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
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / u_resolution.y;

    // Rolling Hill Shape
    float hillHeight = 0.2 * sin(uv.x * 3.0 + 1.0) + 0.1 * cos(uv.x * 5.0 + u_time * 0.1);
    float hillMask = smoothstep(0.4 + hillHeight, 0.39 + hillHeight, uv.y);

    // Night Sky Colors
    vec3 spaceColor = vec3(0.02, 0.05, 0.15);
    vec3 horizonColor = vec3(0.1, 0.1, 0.3);
    vec3 sky = mix(horizonColor, spaceColor, uv.y);

    // Stars
    float starLayer = pow(hash(uv * 500.0), 50.0);
    float twinkle = sin(u_time * 2.0 + uv.x * 100.0) * 0.5 + 0.5;
    sky += starLayer * twinkle;

    // Glowing Nebula Clouds
    float n = fbm(p * 1.5 + u_time * 0.05);
    vec3 nebula = vec3(0.1, 0.0, 0.2) * n;
    sky += nebula;

    // Moon
    vec2 moonPos = vec2(0.6, 0.7);
    float dist = length(p - moonPos);
    float moon = smoothstep(0.15, 0.14, dist);
    float moonGlow = exp(-dist * 8.0) * 0.4;
    sky += (vec3(0.9, 0.9, 1.0) * moon) + (vec3(0.4, 0.5, 0.8) * moonGlow);

    // Dark Rolling Grass
    vec3 grassDark = vec3(0.02, 0.08, 0.02);
    vec3 grassLight = vec3(0.05, 0.15, 0.1);
    float grassVariation = fbm(uv * 10.0 + u_time * 0.2);
    vec3 grassColor = mix(grassDark, grassLight, grassVariation);
    
    // Moon reflection on hills
    float hillHighlight = smoothstep(0.45 + hillHeight, 0.3 + hillHeight, uv.y);
    grassColor += vec3(0.1, 0.2, 0.3) * hillHighlight * 0.5;

    // Combine
    vec3 finalColor = mix(sky, grassColor, hillMask);

    // Vignette
    finalColor *= 1.0 - length(uv - 0.5) * 0.5;

    gl_FragColor = vec4(finalColor, 1.0);
}
