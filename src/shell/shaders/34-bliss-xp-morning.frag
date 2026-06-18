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
        p *= 2.1;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Morning Sky Gradient
    vec3 skyTop = vec3(0.2, 0.55, 0.95);
    vec3 skyBottom = vec3(0.6, 0.85, 1.0);
    vec3 sunColor = vec3(1.0, 0.9, 0.7);
    vec3 color = mix(skyBottom, skyTop, pow(uv.y, 0.8));

    // Distant Sun/Glow
    vec2 sunPos = vec2(aspect * 0.8, 0.65 + sin(u_time * 0.2) * 0.02);
    float dist = length(p - sunPos);
    float sunGlow = exp(-dist * 3.5);
    color += sunColor * sunGlow * 0.5;

    // Moving Clouds
    float cloudMove = u_time * 0.015;
    float clouds = fbm(vec2(p.x * 0.8 + cloudMove, p.y * 2.5 + cloudMove * 0.2));
    float cloudMask = smoothstep(0.45, 0.75, clouds);
    color = mix(color, vec3(1.0), cloudMask * 0.5 * uv.y);

    // Far Hill
    float hill1Y = 0.35 + 0.15 * sin(p.x * 1.0 + 1.2) + 0.04 * fbm(vec2(p.x * 2.0, u_time * 0.01));
    if (uv.y < hill1Y) {
        vec3 grass1 = vec3(0.1, 0.45, 0.1);
        vec3 grass1Light = vec3(0.4, 0.7, 0.2);
        float slope = (hill1Y - uv.y) / 0.4;
        color = mix(grass1Light, grass1, clamp(slope, 0.0, 1.0));
        color *= 0.8 + 0.2 * noise(p * 20.0);
        // Atmospheric haze for depth
        color = mix(color, skyBottom, 0.25);
    }

    // Near Hill (The Iconic Bliss Curve)
    float hill2Y = 0.25 + 0.25 * sin(p.x * 0.7 - 0.8) + 0.02 * fbm(vec2(p.x * 3.0, u_time * 0.005));
    if (uv.y < hill2Y) {
        vec3 grass2 = vec3(0.15, 0.55, 0.05);
        vec3 grass2Light = vec3(0.5, 0.85, 0.1);
        
        // Lighting based on "sun" position
        float lighting = dot(normalize(vec3(1.0, 1.0, 0.0)), normalize(vec3(p.x, uv.y, 1.0)));
        float grad = (hill2Y - uv.y) / 0.5;
        
        color = mix(grass2Light, grass2, clamp(grad + 0.2, 0.0, 1.0));
        color *= 0.9 + 0.15 * noise(p * 40.0);
        
        // Soft Highlight on the ridge
        float ridge = smoothstep(hill2Y - 0.03, hill2Y, uv.y);
        color += ridge * sunColor * 0.15;
    }

    // Morning Bloom / Global Light
    float bloom = smoothstep(0.0, 1.0, 1.0 - length(uv - vec2(0.5, 0.0)));
    color += vec3(1.0, 0.8, 0.5) * bloom * 0.1;

    // Subtle Vignette
    color *= 1.1 - 0.2 * length(uv - 0.5);

    gl_FragColor = vec4(color, 1.0);
}
