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
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / min(u_resolution.y, u_resolution.x);

    // Sky colors (Late Afternoon / Golden Hour)
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyMid = vec3(0.7, 0.8, 1.0);
    vec3 skyBottom = vec3(1.0, 0.7, 0.4);
    
    vec3 sky = mix(skyBottom, skyMid, clamp(uv.y * 2.0, 0.0, 1.0));
    sky = mix(sky, skyTop, clamp(uv.y * 1.5 - 0.5, 0.0, 1.0));

    // Sun / Glow
    float dist = length(uv - vec2(0.8, 0.6));
    float sun = pow(max(0.0, 1.0 - dist * 1.2), 8.0);
    sky += vec3(1.0, 0.8, 0.5) * sun * 0.6;

    // Rolling Green Hills
    float hill1 = 0.3 + 0.15 * sin(uv.x * 2.5 + 1.2) + 0.05 * cos(uv.x * 6.0 + u_time * 0.1);
    float hill2 = 0.2 + 0.12 * sin(uv.x * 3.0 + 4.5) + 0.03 * sin(uv.x * 10.0 - u_time * 0.05);
    float hill3 = 0.1 + 0.1 * sin(uv.x * 2.0 + 0.5);

    vec3 greenDark = vec3(0.1, 0.35, 0.1);
    vec3 greenLight = vec3(0.4, 0.7, 0.2);
    vec3 golden = vec3(0.8, 0.7, 0.2);

    vec3 terrain = sky;

    // Clouds
    float n = fbm(uv * 3.0 + vec2(u_time * 0.02, 0.0));
    vec3 cloudColor = mix(vec3(1.0), vec3(1.0, 0.9, 0.8), uv.y);
    terrain = mix(terrain, cloudColor, smoothstep(0.4, 0.8, n) * 0.4 * uv.y);

    // Render Hills with Depth
    if (uv.y < hill1) {
        float f = fbm(uv * 15.0);
        terrain = mix(greenDark, greenLight, (uv.y / hill1) + f * 0.1);
        terrain = mix(terrain, golden, sun * 0.4);
    }
    if (uv.y < hill2) {
        float f = fbm(uv * 20.0);
        vec3 h2Col = mix(greenDark * 0.8, greenLight * 1.1, (uv.y / hill2) + f * 0.1);
        terrain = mix(terrain, h2Col, 1.0);
        terrain = mix(terrain, golden, sun * 0.3);
    }
    if (uv.y < hill3) {
        float f = fbm(uv * 25.0);
        vec3 h3Col = mix(greenDark * 0.6, greenLight * 0.9, (uv.y / hill3) + f * 0.1);
        terrain = mix(terrain, h3Col, 1.0);
    }

    // Atmospheric Bloom
    terrain += vec3(0.1, 0.05, 0.0) * (1.0 - uv.y);

    gl_FragColor = vec4(terrain, 1.0);
}
