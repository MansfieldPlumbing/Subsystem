precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float noise(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

float smoothNoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = noise(i);
    float b = noise(i + vec2(1.0, 0.0));
    float c = noise(i + vec2(0.0, 1.0));
    float d = noise(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * smoothNoise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = uv * 2.0 - 1.0;
    p.x *= u_resolution.x / u_resolution.y;

    // Rolling green hills
    float hill1 = 0.3 * sin(uv.x * 2.5 + u_time * 0.1) + 0.2 * cos(uv.x * 1.2) - 0.4;
    float hill2 = 0.2 * cos(uv.x * 3.0 - u_time * 0.05) + 0.15 * sin(uv.x * 0.8) - 0.6;
    
    // Sky gradient
    vec3 skyTop = vec3(0.4, 0.7, 1.0);
    vec3 skyBottom = vec3(0.8, 0.9, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Morning Sun glow
    vec2 sunPos = vec2(0.8, 0.8);
    float sun = 1.0 - distance(uv, sunPos);
    sun = pow(max(sun, 0.0), 8.0);
    color += sun * vec3(1.0, 0.9, 0.6) * 0.6;

    // Clouds
    float cloudPattern = fbm(vec2(uv.x * 3.0 + u_time * 0.02, uv.y * 6.0));
    float clouds = smoothstep(0.4, 0.8, cloudPattern);
    color = mix(color, vec3(1.0), clouds * 0.4 * uv.y);

    // Hill layers
    vec3 grassColor1 = vec3(0.2, 0.6, 0.1);
    vec3 grassColor2 = vec3(0.1, 0.4, 0.05);
    
    // Detail on grass
    float grassDetail = fbm(uv * 20.0);
    grassColor1 += grassDetail * 0.05;
    grassColor2 += grassDetail * 0.05;

    if (p.y < hill1) {
        color = mix(grassColor1, skyBottom, clamp((hill1 - p.y) * 0.5, 0.0, 0.1));
    }
    if (p.y < hill2) {
        color = mix(grassColor2, skyBottom, clamp((hill2 - p.y) * 0.5, 0.0, 0.1));
    }

    // Vignette
    float vignette = length(p * 0.5);
    color *= 1.0 - vignette * 0.2;

    gl_FragColor = vec4(color, 1.0);
}
