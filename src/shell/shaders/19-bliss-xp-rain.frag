precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), f.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Moody Bliss Sky
    vec3 skyTop = vec3(0.15, 0.3, 0.5);
    vec3 skyBottom = vec3(0.4, 0.55, 0.75);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Dynamic Clouds
    float cloudDensity = noise(p * 2.5 + u_time * 0.05);
    cloudDensity += 0.5 * noise(p * 5.0 - u_time * 0.02);
    color = mix(color, vec4(0.8, 0.8, 0.9, 1.0).rgb, smoothstep(0.4, 0.9, cloudDensity) * 0.4 * uv.y);

    // Far Hill Layer
    float h1 = 0.38 + 0.08 * sin(p.x * 1.5 + 0.4) + 0.04 * sin(p.x * 4.0 + u_time * 0.1);
    if (uv.y < h1) {
        float grad = uv.y / h1;
        vec3 hillColor = mix(vec3(0.1, 0.35, 0.15), vec3(0.2, 0.5, 0.2), grad);
        color = mix(color, hillColor, 1.0);
        // Wet hill reflection/lighting
        color += 0.05 * sin(p.x * 10.0 + u_time) * grad;
    }

    // Near Hill Layer (The iconic Bliss hill)
    float h2 = 0.22 + 0.12 * cos(p.x * 1.2 - 1.2) + 0.06 * sin(p.x * 3.2);
    if (uv.y < h2) {
        float grad = uv.y / h2;
        vec3 hillColor = mix(vec3(0.05, 0.25, 0.05), vec3(0.3, 0.7, 0.2), grad);
        color = hillColor;
        // Subtle grass texture
        color *= 0.9 + 0.1 * noise(p * 50.0);
    }

    // Rain Streaks
    vec2 rainUv = uv;
    rainUv.x += rainUv.y * 0.15; // Slant for wind
    float rainSpeed = u_time * 4.5;
    
    // Create multiple layers of rain
    for(float i = 1.0; i < 4.0; i++) {
        float scale = i * 40.0;
        vec2 rv = rainUv * vec2(scale, scale * 0.1);
        rv.y -= rainSpeed * (1.0 + i * 0.2);
        
        float r = hash(vec2(floor(rv.x), 0.0));
        float streak = smoothstep(0.85, 1.0, fract(rv.y + r));
        streak *= step(0.95, hash(vec2(floor(rv.x), floor(rv.y))));
        
        color += streak * 0.2 * (1.0 / i);
    }

    // Fog / Mist at the bottom
    color = mix(color, vec3(0.6, 0.65, 0.7), (1.0 - uv.y) * 0.2);

    // Vignette
    float vignette = uv.x * uv.y * (1.0 - uv.x) * (1.0 - uv.y);
    color *= pow(vignette * 15.0, 0.1);

    gl_FragColor = vec4(color, 1.0);
}
