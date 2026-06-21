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
    vec2 p = (gl_FragCoord.xy - 0.5 * u_resolution.xy) / min(u_resolution.y, u_resolution.x);
    
    // Background: Bliss XP inspired gradients
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.4, 0.7, 1.0);
    vec3 groundColor = vec3(0.1, 0.4, 0.1);
    
    float horizon = 0.2 * sin(uv.x * 3.0 + u_time * 0.2) - 0.2;
    float mask = smoothstep(horizon - 0.02, horizon + 0.02, uv.y - 0.4);
    
    // Stormy mood shift
    float stormIntensity = sin(u_time * 0.5) * 0.5 + 0.5;
    skyTop = mix(skyTop, vec3(0.05, 0.05, 0.1), stormIntensity);
    skyBottom = mix(skyBottom, vec3(0.2, 0.2, 0.3), stormIntensity);
    groundColor = mix(groundColor, vec3(0.02, 0.1, 0.02), stormIntensity);
    
    vec3 color = mix(groundColor, mix(skyBottom, skyTop, uv.y), mask);

    // Tornado logic
    vec2 tornadoPos = p;
    tornadoPos.x += sin(u_time * 1.5 + p.y * 2.0) * 0.15; // Sway
    float dist = abs(tornadoPos.x);
    float funnel = smoothstep(0.4, 0.0, dist - (p.y + 0.5) * 0.3);
    funnel *= smoothstep(-0.5, 0.8, p.y);
    
    float spiral = fbm(vec2(tornadoPos.x * 5.0, tornadoPos.y * 2.0 - u_time * 8.0));
    vec3 tornadoColor = vec3(0.3, 0.3, 0.35) * spiral;
    color = mix(color, tornadoColor, funnel * 0.8);

    // Lightning flashes
    float flash = step(0.98, sin(u_time * 5.0 + hash(vec2(floor(u_time * 10.0))))) * hash(vec2(u_time));
    vec2 boltPos = p - vec2(sin(u_time * 20.0) * 0.5, 0.5);
    float bolt = smoothstep(0.015, 0.0, abs(boltPos.x + fbm(p * 10.0 + u_time) * 0.2));
    color += flash * vec3(0.8, 0.8, 1.0) * bolt;
    color += flash * 0.2; // Screen flash

    // Clouds
    float clouds = fbm(p * 2.0 + u_time * 0.1);
    color = mix(color, vec3(0.1, 0.1, 0.15), clouds * stormIntensity * mask);

    // Vignette
    color *= 1.2 - dot(p, p);

    gl_FragColor = vec4(color, 1.0);
}
