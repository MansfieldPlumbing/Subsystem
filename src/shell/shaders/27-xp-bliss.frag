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
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Sky gradient
    vec3 skyTop = vec3(0.0, 0.35, 0.85);
    vec3 skyBottom = vec3(0.5, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, pow(uv.y, 0.8));

    // Sun/Glow
    float sun = smoothstep(0.5, 0.0, length(uv - vec2(0.8, 0.85)));
    color += vec3(1.0, 0.9, 0.7) * sun * 0.3;

    // Clouds
    vec2 cloudUV = p * 1.5 + vec2(u_time * 0.03, 0.0);
    float c1 = fbm(cloudUV);
    float c2 = fbm(cloudUV * 1.5 + 10.0);
    float cloudMask = smoothstep(0.4, 0.7, c1 * c2);
    cloudMask *= smoothstep(0.3, 0.6, uv.y); // Fade clouds towards horizon
    color = mix(color, vec4(1.0, 1.0, 1.0, 1.0).rgb, cloudMask * 0.8);

    // Hill Layers
    // Layer 1 (Background Hill)
    float h1 = 0.3 + 0.12 * sin(p.x * 1.2 + 2.0) + 0.05 * noise(vec2(p.x * 2.0, 0.0));
    vec3 hill1Base = vec3(0.1, 0.5, 0.1);
    vec3 hill1Top = vec3(0.4, 0.7, 0.2);
    float hill1Mask = smoothstep(h1, h1 - 0.005, uv.y);
    
    // Layer 2 (Foreground Hill)
    float h2 = 0.2 + 0.18 * sin(p.x * 0.8 - 0.5) + 0.02 * noise(vec2(p.x * 4.0, 5.0));
    vec3 hill2Base = vec3(0.05, 0.4, 0.0);
    vec3 hill2Top = vec3(0.3, 0.8, 0.1);
    float hill2Mask = smoothstep(h2, h2 - 0.005, uv.y);

    // Apply Background Hill
    vec3 hill1Color = mix(hill1Base, hill1Top, clamp(uv.y / h1, 0.0, 1.0));
    hill1Color += (noise(p * 80.0) - 0.5) * 0.05; // Grass texture
    color = mix(color, hill1Color, hill1Mask);

    // Apply Foreground Hill
    vec3 hill2Color = mix(hill2Base, hill2Top, clamp(uv.y / h2, 0.0, 1.0));
    hill2Color += (noise(p * 100.0) - 0.5) * 0.06; // Grass texture
    // Shadow where hill 2 meets hill 1
    float shadow = smoothstep(h2 + 0.05, h2, uv.y + 0.02);
    color = mix(color, hill2Color, hill2Mask);
    
    // Subtle atmospheric haze
    color = mix(color, skyBottom, (1.0 - uv.y) * 0.15);

    // Vignette
    float vig = uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y);
    vig = pow(vig * 16.0, 0.1);
    color *= vig;

    gl_FragColor = vec4(color, 1.0);
}
