precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(float n) { return fract(sin(n) * 43758.5453123); }

float noise(vec2 x) {
    vec2 p = floor(x);
    vec2 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    float n = p.x + p.y * 57.0;
    return mix(mix(hash(n + 0.0), hash(n + 1.0), f.x),
               mix(hash(n + 57.0), hash(n + 58.0), f.x), f.y);
}

float fbm(vec2 p) {
    float f = 0.0;
    f += 0.5000 * noise(p); p = p * 2.02;
    f += 0.2500 * noise(p); p = p * 2.03;
    f += 0.1250 * noise(p); p = p * 2.01;
    f += 0.0625 * noise(p);
    return f / 0.9375;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Time-based eclipse cycle
    float cycle = sin(u_time * 0.2) * 0.5 + 0.5;
    vec2 sunPos = vec2(0.8 * aspect, 0.75);
    vec2 moonPos = sunPos + vec2((cycle - 0.5) * 0.15, 0.0);
    
    // Background Sky
    vec3 skyDay = vec3(0.4, 0.7, 1.0);
    vec3 skyNight = vec3(0.02, 0.05, 0.15);
    float eclipseFactor = smoothstep(0.05, 0.0, abs(cycle - 0.5));
    vec3 skyCol = mix(skyDay, skyNight, eclipseFactor);
    
    // Clouds
    float n = fbm(p * 3.0 + u_time * 0.05);
    skyCol = mix(skyCol, vec3(1.0), n * 0.3 * (1.0 - eclipseFactor));

    // Corona
    float distSun = length(p - sunPos);
    float corona = 0.0;
    if (eclipseFactor > 0.8) {
        float angle = atan(p.y - sunPos.y, p.x - sunPos.x);
        float beam = fbm(vec2(angle * 3.0, u_time * 0.5));
        corona = pow(0.03 / distSun, 1.2) * beam * 2.0;
        skyCol += vec3(0.8, 0.9, 1.0) * corona * eclipseFactor;
    }

    // Sun and Moon
    float sunCirc = smoothstep(0.1, 0.09, distSun);
    float moonCirc = smoothstep(0.1, 0.09, length(p - moonPos));
    
    vec3 sunCol = vec3(1.0, 0.95, 0.8) * sunCirc;
    skyCol = mix(skyCol, sunCol, sunCirc);
    skyCol = mix(skyCol, skyNight * 0.5, moonCirc);

    // Bliss Hills
    float hill1 = 0.3 + 0.12 * sin(p.x * 1.5 + 1.0) + 0.05 * cos(p.x * 2.5);
    float hill2 = 0.2 + 0.10 * sin(p.x * 2.0 - 0.5) + 0.04 * sin(p.x * 4.0);
    float hill3 = 0.1 + 0.08 * cos(p.x * 3.0 + 2.0) + 0.03 * sin(p.x * 6.0);

    vec3 grass1 = vec3(0.2, 0.6, 0.1);
    vec3 grass2 = vec3(0.3, 0.7, 0.2);
    vec3 grass3 = vec3(0.4, 0.8, 0.3);

    // Darken hills during eclipse
    float lightIntensity = mix(1.0, 0.15, eclipseFactor);
    grass1 *= lightIntensity;
    grass2 *= lightIntensity;
    grass3 *= lightIntensity;

    if (p.y < hill1) {
        float grad = (p.y / hill1);
        skyCol = mix(grass1 * 0.6, grass1, grad);
        skyCol += noise(p * 50.0) * 0.03;
    }
    if (p.y < hill2) {
        float grad = (p.y / hill2);
        skyCol = mix(grass2 * 0.6, grass2, grad);
        skyCol += noise(p * 60.0) * 0.03;
    }
    if (p.y < hill3) {
        float grad = (p.y / hill3);
        skyCol = mix(grass3 * 0.6, grass3, grad);
        skyCol += noise(p * 70.0) * 0.03;
    }

    // Stars during total eclipse
    if (eclipseFactor > 0.9) {
        float stars = pow(hash(p.x * 123.4 + p.y * 567.8), 50.0);
        skyCol += stars * (eclipseFactor - 0.9) * 10.0;
    }

    // Lens Flare
    float flare = max(0.0, 1.0 - length(p - sunPos) * 2.0);
    skyCol += vec3(1.0, 0.8, 0.6) * pow(flare, 4.0) * (1.0 - eclipseFactor);

    gl_FragColor = vec4(skyCol, 1.0);
}
