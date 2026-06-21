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

float lightning(vec2 uv, float t) {
    float timeMod = fract(t * 0.5);
    float burst = step(0.95, hash(vec2(floor(t * 10.0), 0.0)));
    if (burst < 0.5) return 0.0;
    
    float xOffset = hash(vec2(floor(t * 10.0), 1.0)) * 0.6 - 0.3;
    uv.x -= xOffset + 0.5;
    
    float bolt = abs(uv.x + (noise(vec2(uv.y * 5.0, t)) - 0.5) * 0.3);
    float glow = exp(-bolt * 50.0);
    float core = exp(-bolt * 500.0);
    
    return (glow * 0.4 + core) * hash(vec2(t, t));
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Stormy Sky Base
    vec3 skyDark = vec3(0.02, 0.04, 0.1);
    vec3 skyLight = vec3(0.15, 0.2, 0.3);
    vec3 color = mix(skyDark, skyLight, uv.y);

    // Dynamic Clouds
    float cloudMove = u_time * 0.1;
    float c1 = fbm(p * 2.0 + vec2(cloudMove, 0.0));
    float c2 = fbm(p * 4.0 - vec2(cloudMove * 0.5, 0.1));
    color = mix(color, vec3(0.1, 0.1, 0.15), c1 * 0.8);
    color = mix(color, vec3(0.05, 0.05, 0.1), c2 * 0.5);

    // Bliss-style Rolling Hills
    float hill1 = 0.35 + 0.15 * sin(p.x * 1.5 + 0.5) + 0.05 * sin(p.x * 4.0);
    float hill2 = 0.25 + 0.12 * sin(p.x * 1.2 - 1.0) + 0.03 * cos(p.x * 3.5);
    
    // Greenery with storm shading
    vec3 hillColor1 = vec3(0.1, 0.35, 0.1);
    vec3 hillColor2 = vec3(0.05, 0.25, 0.05);
    
    // Lightning calculation
    float l = lightning(uv, u_time);
    float flash = step(0.96, hash(vec2(floor(u_time * 15.0), 0.0))) * hash(vec2(u_time, 1.0));
    
    // Hill layers
    if (uv.y < hill1) {
        float grad = (uv.y / hill1);
        vec3 green = mix(hillColor1 * 0.5, hillColor1, grad);
        color = green;
        // Grass texture
        color += (noise(p * 50.0) - 0.5) * 0.05;
    }
    if (uv.y < hill2) {
        float grad = (uv.y / hill2);
        vec3 green = mix(hillColor2 * 0.4, hillColor2, grad);
        color = green;
        color += (noise(p * 60.0 + 10.0) - 0.5) * 0.04;
    }

    // Lightning illumination
    color += vec3(0.5, 0.6, 1.0) * l;
    color += vec3(0.2, 0.25, 0.4) * flash;

    // Rain streaks
    float rain = fract(sin(dot(p + vec2(0.0, u_time * 3.0), vec2(12.9898, 78.233))) * 43758.5453);
    if (rain > 0.98) {
        color += 0.1;
    }

    // Atmospheric fog/mist at the bottom
    float mist = (1.0 - uv.y) * 0.2;
    color = mix(color, vec3(0.1, 0.12, 0.2), mist);

    // Vignette
    float vig = uv.x * (1.0 - uv.x) * uv.y * (1.0 - uv.y) * 15.0;
    color *= pow(vig, 0.2);

    gl_FragColor = vec4(color, 1.0);
}
