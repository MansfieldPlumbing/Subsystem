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

    // Time-based slow wind
    float t = u_time * 0.2;

    // Sky Gradient: Late afternoon palette
    vec3 skyTop = vec3(0.1, 0.3, 0.7);
    vec3 skyBottom = vec3(1.0, 0.7, 0.4);
    vec3 sky = mix(skyBottom, skyTop, pow(uv.y, 0.8));

    // Sun position (low in the sky)
    vec2 sunPos = vec2(0.8 * aspect, 0.45);
    float sunDist = length(p - sunPos);
    float sunSize = 0.06;
    float sunGlow = exp(-sunDist * 8.0) * 0.6;
    float sunDisc = smoothstep(sunSize, sunSize - 0.01, sunDist);
    
    vec3 sunColor = vec3(1.0, 0.9, 0.6);
    sky += sunColor * sunDisc;
    sky += vec3(1.0, 0.4, 0.1) * sunGlow;

    // Clouds
    float cloudNoise = fbm(vec2(p.x * 1.5 + t * 0.5, p.y * 4.0));
    float clouds = smoothstep(0.4, 0.7, cloudNoise) * smoothstep(0.3, 0.8, uv.y);
    sky = mix(sky, vec3(1.0, 0.95, 0.9), clouds * 0.4);

    // Hill 1 (Far)
    float h1 = 0.35 + 0.1 * sin(p.x * 1.5 + 1.0) + 0.05 * cos(p.x * 2.8);
    vec3 hill1Color = vec3(0.2, 0.5, 0.2);
    hill1Color = mix(hill1Color, skyBottom, 0.4); // Atmospheric haze

    // Hill 2 (Middle - The "Bliss" Hill)
    float h2 = 0.25 + 0.15 * sin(p.x * 1.1 - 0.5) + 0.06 * sin(p.x * 2.5 + t * 0.1);
    vec3 hill2Color = vec3(0.15, 0.6, 0.1);
    // Add golden lighting from the sun
    float hill2Light = smoothstep(0.0, 0.5, h2 - p.y + 0.1) * (1.0 - p.x / aspect);
    hill2Color += vec3(0.4, 0.3, 0.0) * hill2Light;

    // Hill 3 (Foreground)
    float h3 = 0.1 + 0.12 * sin(p.x * 0.8 + 2.0) + 0.04 * cos(p.x * 4.0 - t * 0.2);
    vec3 hill3Color = vec3(0.05, 0.3, 0.05);

    // Compositing
    vec3 finalColor = sky;

    if (uv.y < h1) {
        finalColor = hill1Color;
    }
    if (uv.y < h2) {
        // Simple grass texture/shading
        float grass = noise(p * 50.0) * 0.1;
        finalColor = hill2Color + grass;
        // Edge highlight
        float edge = smoothstep(0.01, 0.0, h2 - uv.y);
        finalColor += vec3(0.5, 0.4, 0.1) * edge * 0.5;
    }
    if (uv.y < h3) {
        finalColor = mix(hill3Color, vec3(0.0, 0.1, 0.0), uv.y / h3);
    }

    // Vignette and Warmth
    float vignette = uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 15.0;
    finalColor *= pow(vignette, 0.1);
    finalColor *= vec3(1.05, 1.0, 0.9); // Slight warm tint

    gl_FragColor = vec4(finalColor, 1.0);
}
