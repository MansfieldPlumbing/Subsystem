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

    // Sky gradient: vibrant blue to light blue
    vec3 skyTop = vec4(0.1, 0.45, 0.9, 1.0).rgb;
    vec3 skyBottom = vec4(0.5, 0.75, 1.0, 1.0).rgb;
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Soft clouds
    vec2 cloudUV = vec2(p.x * 0.4 + u_time * 0.015, p.y);
    float n = fbm(cloudUV * 3.5);
    float cloudMask = smoothstep(0.45, 0.85, n * (uv.y + 0.3));
    color = mix(color, vec3(1.0), cloudMask * 0.7);

    // Far hills
    float hill1_y = 0.38 + 0.08 * sin(p.x * 1.2 + 0.5) + 0.02 * noise(p * 5.0 + u_time * 0.05);
    vec3 hill1Color = mix(vec3(0.2, 0.5, 0.1), vec3(0.4, 0.75, 0.2), uv.y * 2.5);
    float mask1 = smoothstep(hill1_y, hill1_y - 0.005, uv.y);
    color = mix(color, hill1Color, mask1);

    // Mid hills
    float hill2_y = 0.28 + 0.12 * cos(p.x * 0.9 - 1.2) + 0.015 * noise(p * 4.0);
    vec3 hill2Color = mix(vec3(0.15, 0.45, 0.05), vec3(0.35, 0.8, 0.15), (uv.y + 0.1) * 2.5);
    float mask2 = smoothstep(hill2_y, hill2_y - 0.005, uv.y);
    color = mix(color, hill2Color, mask2);

    // Foreground hill (The Bliss Hill)
    float hill3_y = 0.18 + 0.15 * sin(p.x * 0.7 + u_time * 0.01) + 0.01 * noise(p * 6.0);
    vec3 hill3Color = mix(vec3(0.1, 0.4, 0.0), vec3(0.45, 0.85, 0.1), (uv.y + 0.2) * 2.2);
    
    // Add a slight "sheen" to the hill for that XP look
    float sheen = pow(1.0 - abs(uv.y - hill3_y), 15.0) * 0.2;
    hill3Color += sheen;
    
    float mask3 = smoothstep(hill3_y, hill3_y - 0.008, uv.y);
    color = mix(color, hill3Color, mask3);

    // Subtle sun glow
    float sun = max(0.0, 1.0 - distance(uv, vec2(0.85, 0.85)) * 1.5);
    color += vec3(1.0, 0.95, 0.7) * pow(sun, 3.0) * 0.4;

    // Vignette
    float vignette = uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 15.0;
    color *= pow(vignette, 0.05);

    gl_FragColor = vec4(color, 1.0);
}
