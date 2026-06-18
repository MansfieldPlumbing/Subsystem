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

    // Deep night sky gradient
    vec3 skyTop = vec3(0.005, 0.01, 0.03);
    vec3 skyBottom = vec3(0.02, 0.05, 0.15);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Subtle drifting clouds
    float cloudAlpha = fbm(p * 2.0 + u_time * 0.05);
    color = mix(color, vec3(0.1, 0.15, 0.3), cloudAlpha * 0.3 * uv.y);

    // Twinkling stars
    float starSelection = hash(floor(p * 50.0));
    if (starSelection > 0.99) {
        float twinkle = sin(u_time * 3.0 + starSelection * 100.0) * 0.5 + 0.5;
        color += vec3(0.8, 0.9, 1.0) * twinkle * (1.0 - uv.y);
    }

    // Glowing Moon
    vec2 moonPos = vec2(0.3 * aspect, 0.8);
    float dToMoon = length(p - moonPos);
    float moonMask = smoothstep(0.08, 0.075, dToMoon);
    float moonGlow = exp(-dToMoon * 10.0) * 0.6;
    color += vec3(0.9, 0.95, 1.0) * (moonMask + moonGlow);
    
    // Moon crater details
    if (dToMoon < 0.08) {
        float craters = fbm(p * 15.0);
        color = mix(color, color * 0.8, craters * 0.4);
    }

    // Iconic Rolling Hills
    // Background Hill
    float h1 = 0.35 + 0.12 * sin(p.x * 1.5 + 0.5) + 0.05 * cos(p.x * 3.1);
    float hill1Mask = smoothstep(h1 + 0.005, h1, uv.y);
    vec3 hill1Color = mix(vec3(0.01, 0.04, 0.08), vec3(0.02, 0.1, 0.05), (uv.y / h1));
    hill1Color += vec3(0.05, 0.1, 0.2) * (1.0 - abs(uv.y - h1) * 20.0); // Rim light
    color = mix(color, hill1Color, hill1Mask);

    // Foreground Hill (The Bliss Curve)
    float h2 = 0.25 + 0.2 * sin(p.x * 0.8 - 0.8) + 0.03 * sin(p.x * 4.0);
    float hill2Mask = smoothstep(h2 + 0.005, h2, uv.y);
    
    // Dark green grass texture for foreground
    float grassTex = fbm(p * 20.0 + vec2(u_time * 0.1, 0.0));
    vec3 hill2Color = mix(vec3(0.005, 0.03, 0.01), vec3(0.02, 0.12, 0.04), uv.y / h2);
    hill2Color *= 0.8 + 0.2 * grassTex;
    
    // Soft moonlight highlight on the main ridge
    float ridgeHighlight = smoothstep(0.05, 0.0, abs(uv.y - h2));
    hill2Color += vec3(0.1, 0.2, 0.25) * ridgeHighlight * (1.0 - uv.y);
    
    color = mix(color, hill2Color, hill2Mask);

    // Fog in the valleys
    float fog = exp(-uv.y * 4.0) * 0.15;
    color += vec3(0.1, 0.15, 0.25) * fog * (1.0 - hill2Mask);

    // Vignette
    color *= smoothstep(1.5, 0.5, length(uv - 0.5));

    gl_FragColor = vec4(color, 1.0);
}
