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
    mat2 m = mat2(1.6, 1.2, -1.2, 1.6);
    for (int i = 0; i < 5; i++) {
        v += a * noise(p);
        p = m * p;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / min(u_resolution.y, u_resolution.x);

    // Heat distortion
    uv.x += sin(uv.y * 10.0 + u_time * 2.0) * 0.01;
    uv.y += cos(uv.x * 10.0 + u_time * 1.5) * 0.005;

    // Bliss hill shape
    float hillHeight = 0.25 + 0.15 * sin(uv.x * 3.5 - 0.5) + 0.05 * cos(uv.x * 8.0);
    float hillMask = smoothstep(hillHeight + 0.01, hillHeight - 0.01, uv.y);

    // Fire Sky
    vec3 skyTop = vec3(0.1, 0.0, 0.0);
    vec3 skyBottom = vec3(0.8, 0.2, 0.0);
    vec3 skyColor = mix(skyBottom, skyTop, uv.y);
    
    float skyNoise = fbm(uv * 3.0 + vec2(u_time * 0.2, -u_time * 0.5));
    skyColor += vec3(1.0, 0.4, 0.0) * pow(skyNoise, 2.0) * 1.5;

    // Scorched Grass Hill
    vec3 grassColor = mix(vec3(0.05, 0.15, 0.0), vec3(0.2, 0.4, 0.1), uv.y / hillHeight);
    float scorch = fbm(uv * 10.0 + u_time * 0.1);
    grassColor = mix(grassColor, vec3(0.1, 0.05, 0.0), scorch);
    
    // Glowing edge of the hill
    float edgeGlow = smoothstep(0.05, 0.0, abs(uv.y - hillHeight));
    vec3 fireEdge = vec3(1.0, 0.6, 0.1) * edgeGlow * (0.8 + 0.4 * sin(u_time * 5.0 + uv.x * 20.0));

    // Flames licking the hill
    float flameNoise = fbm(uv * vec2(15.0, 5.0) - vec2(u_time * 0.5, u_time * 3.0));
    float flameMask = smoothstep(hillHeight - 0.05, hillHeight + 0.2, uv.y) * hillMask;
    vec3 flames = vec3(1.0, 0.3, 0.0) * pow(flameNoise, 1.5) * flameMask * 2.0;

    // Embers
    float embers = pow(hash(uv + fract(u_time * 0.1)), 100.0);
    vec3 emberColor = vec3(1.0, 0.8, 0.2) * embers * step(0.99, hash(uv + u_time));

    // Final Composition
    vec3 finalColor = mix(skyColor, grassColor, hillMask);
    finalColor += fireEdge * hillMask;
    finalColor += flames;
    finalColor += emberColor;

    // Vignette and contrast
    float vignette = 1.0 - dot(p, p) * 0.2;
    finalColor *= vignette;
    
    gl_FragColor = vec4(finalColor, 1.0);
}
